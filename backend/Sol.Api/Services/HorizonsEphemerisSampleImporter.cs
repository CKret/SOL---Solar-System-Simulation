using Microsoft.Data.SqlClient;
using Sol.Api.Models;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sol.Api.Services;

public sealed partial class HorizonsEphemerisSampleImporter(
  HttpClient httpClient,
  ISqlWriteConnectionFactory connectionFactory) : IEphemerisSampleImporter
{
  private const string HorizonsApiBase = "https://ssd.jpl.nasa.gov/api/horizons.api";

  private readonly HttpClient _httpClient = httpClient;
  private readonly ISqlWriteConnectionFactory _connectionFactory = connectionFactory;

  public async Task<EphemerisSampleImportResult> ImportAsync(DateTime startUtc, DateTime endUtc, TimeSpan? sampleRateOverride, CancellationToken cancellationToken)
  {
    var normalizedStartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
    var normalizedEndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);

    if (normalizedEndUtc < normalizedStartUtc) {
      throw new ArgumentException("endUtc must be greater than or equal to startUtc.");
    }

    if (sampleRateOverride is not null && sampleRateOverride <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(sampleRateOverride), "sampleRateOverride must be greater than zero when provided.");
    }

    var bodyInfoBySlug = await LoadBodyIdsAsync(cancellationToken);
    var allSamples = new List<SampleImportRow>();

    foreach (var target in AuthoritativeCatalogManifest.Targets) {
      if (!bodyInfoBySlug.TryGetValue(target.Slug, out var info)) {
        throw new InvalidOperationException($"Active body '{target.Slug}' was not found in dbo.Bodies. Run import-bodies first.");
      }

      var horizonsCommand = info.JplHorizonsId ?? target.HorizonsCommand;
      var samples = await GetSamplesForTargetAsync(target, info.BodyId, horizonsCommand, normalizedStartUtc, normalizedEndUtc, sampleRateOverride, cancellationToken);
      allSamples.AddRange(samples);
    }

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

    var deleted = await DeleteExistingSamplesAsync(connection, transaction, bodyInfoBySlug.Values.Select(v => v.BodyId), normalizedStartUtc, normalizedEndUtc, cancellationToken);
    var inserted = await InsertSamplesAsync(connection, transaction, allSamples, cancellationToken);

    await transaction.CommitAsync(cancellationToken);
    return new EphemerisSampleImportResult(bodyInfoBySlug.Count, inserted, deleted);
  }

  public async Task<(int Bodies, int Samples)> ImportMpcorbSamplesAsync(
      double? hMax, DateTime startUtc, DateTime endUtc, TimeSpan step,
      CancellationToken cancellationToken)
  {
    var bodies = await LoadMpcorbBodyListAsync(hMax, cancellationToken);
    Console.WriteLine($"Queued {bodies.Count:N0} MPC bodies for ephemeris import.");

    int processedBodies = 0, totalSamples = 0;

    for (var i = 0; i < bodies.Count; i++) {
      var (bodyId, slug, jplId) = bodies[i];
      if (string.IsNullOrWhiteSpace(jplId)) {
        Console.WriteLine($"  [{i + 1}/{bodies.Count}] SKIP {slug}: no JplHorizonsId");
        continue;
      }

      try {
        var samples = await FetchMpcorbBodySamplesAsync(bodyId, slug, jplId, startUtc, endUtc, step, cancellationToken);

        if (samples.Count > 0) {
          await using var conn = _connectionFactory.CreateConnection();
          await conn.OpenAsync(cancellationToken);
          await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
          await DeleteExistingSamplesAsync(conn, tx, [bodyId], startUtc, endUtc, cancellationToken);
          await InsertSamplesAsync(conn, tx, samples, cancellationToken);
          await MarkHasEphemerisAsync(conn, tx, bodyId, cancellationToken);
          await tx.CommitAsync(cancellationToken);

          processedBodies++;
          totalSamples += samples.Count;
          Console.WriteLine($"  [{i + 1}/{bodies.Count}] {slug}: {samples.Count:N0} samples");
        }
        else {
          Console.WriteLine($"  [{i + 1}/{bodies.Count}] SKIP {slug}: 0 samples returned");
        }
      }
      catch (Exception ex) {
        Console.WriteLine($"  [{i + 1}/{bodies.Count}] ERROR {slug}: {ex.Message}");
      }

      await Task.Delay(300, cancellationToken);
    }

    return (processedBodies, totalSamples);
  }

  private async Task<List<(int BodyId, string Slug, string? JplId)>> LoadMpcorbBodyListAsync(
      double? hMax, CancellationToken cancellationToken)
  {
    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(cancellationToken);

    var sql = "SELECT BodyId, Slug, JplHorizonsId FROM dbo.Bodies"
            + " WHERE Source = 'mpcorb' AND IsActive = 1 AND HasEphemeris = 0"
            + (hMax.HasValue ? " AND H_AbsMag <= @hMax" : "")
            + " ORDER BY H_AbsMag ASC, Slug";

    await using var cmd = new SqlCommand(sql, conn);
    if (hMax.HasValue) cmd.Parameters.AddWithValue("@hMax", hMax.Value);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

    var list = new List<(int, string, string?)>();
    while (await reader.ReadAsync(cancellationToken))
      list.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
    return list;
  }

  // JPL Horizons rejects requests whose projected output exceeds 90,024 lines.
  // Split the requested range into windows that each stay safely below that limit.
  private static IEnumerable<(DateTime Start, DateTime End)> ChunkRange(DateTime start, DateTime end, TimeSpan step)
  {
    const int maxLinesPerRequest = 87000;
    var windowSpan = TimeSpan.FromTicks((long)(maxLinesPerRequest * step.Ticks));
    var windowStart = start;
    while (windowStart < end) {
      var windowEnd = windowStart + windowSpan;
      if (windowEnd > end) windowEnd = end;
      yield return (windowStart, windowEnd);
      // Advance by step so the boundary date is only included once (in the window that ends on it).
      windowStart = windowEnd + step;
    }
  }

  private async Task<IReadOnlyList<SampleImportRow>> FetchMpcorbBodySamplesAsync(
      int bodyId, string slug, string horizonsCommand,
      DateTime startUtc, DateTime endUtc, TimeSpan step,
      CancellationToken cancellationToken)
  {
    var allSamples = new List<SampleImportRow>();

    foreach (var (winStart, winEnd) in ChunkRange(startUtc, endUtc, step)) {
      var requestUri = BuildHorizonsVectorsUri(horizonsCommand, winStart, winEnd, step);
      using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
      if (!response.IsSuccessStatusCode)
        return allSamples;

      await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

      if (!doc.RootElement.TryGetProperty("result", out var resultEl))
        return allSamples;

      var resultText = resultEl.GetString();
      if (string.IsNullOrEmpty(resultText))
        return allSamples;

      // No $$SOE means JPL returned an error (ambiguous body, not found, etc.) — stop.
      if (!resultText.Contains("$$SOE"))
        return allSamples;

      allSamples.AddRange(ParseHorizonsVectorCsv(bodyId, resultText, slug));

      if (winEnd < endUtc)
        await Task.Delay(150, cancellationToken);
    }
    // Deduplicate on timestamp in case window boundaries produced any overlap.
    return allSamples
      .GroupBy(s => s.SampleTimeUtc)
      .Select(g => g.First())
      .OrderBy(s => s.SampleTimeUtc)
      .ToList();
  }

  private static async Task MarkHasEphemerisAsync(
      SqlConnection conn, SqlTransaction tx, int bodyId, CancellationToken cancellationToken)
  {
    const string sql = "UPDATE dbo.Bodies SET HasEphemeris = 1, UpdatedUtc = SYSUTCDATETIME() WHERE BodyId = @id;";
    await using var cmd = new SqlCommand(sql, conn, tx);
    cmd.Parameters.AddWithValue("@id", bodyId);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<Dictionary<string, (int BodyId, string? JplHorizonsId)>> LoadBodyIdsAsync(CancellationToken cancellationToken)
  {
    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    const string sql = "SELECT BodyId, Slug, JplHorizonsId FROM dbo.Bodies WHERE IsActive = 1;";
    await using var command = new SqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);

    var result = new Dictionary<string, (int BodyId, string? JplHorizonsId)>(StringComparer.OrdinalIgnoreCase);
    while (await reader.ReadAsync(cancellationToken)) {
      result[reader.GetString(1)] = (reader.GetInt32(0), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    return result;
  }

  private async Task<IReadOnlyList<SampleImportRow>> GetSamplesForTargetAsync(AuthoritativeCatalogTarget target, int bodyId, string? horizonsCommand, DateTime startUtc, DateTime endUtc, TimeSpan? sampleRateOverride, CancellationToken cancellationToken)
  {
    var samples = new Dictionary<DateTime, SampleImportRow>();

    foreach (var window in EphemerisImportSourcePolicy.GetWindowsForTarget(target.Slug, startUtc, endUtc, sampleRateOverride)) {
      foreach (var sample in await FetchHorizonsSamplesAsync(target, bodyId, horizonsCommand, window.StartUtc, window.EndUtc, window.Step, cancellationToken)) {
        samples[sample.SampleTimeUtc] = sample;
      }
    }

    return samples.Values.OrderBy(sample => sample.SampleTimeUtc).ToArray();
  }

  private async Task<IReadOnlyList<SampleImportRow>> FetchHorizonsSamplesAsync(AuthoritativeCatalogTarget target, int bodyId, string? horizonsCommand, DateTime startUtc, DateTime endUtc, TimeSpan step, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(horizonsCommand)) {
      throw new InvalidOperationException($"Target '{target.Slug}' has no JplHorizonsId in dbo.Bodies. Run import-bodies first.");
    }

    var requestUri = BuildHorizonsVectorsUri(horizonsCommand, startUtc, endUtc, step);
    using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    var resultText = document.RootElement.GetProperty("result").GetString()
      ?? throw new InvalidOperationException($"Horizons vectors response did not include result text for '{target.Slug}'.");

    return ParseHorizonsVectorCsv(bodyId, resultText, target.Slug);
  }

  private static IReadOnlyList<SampleImportRow> ParseHorizonsVectorCsv(int bodyId, string resultText, string slug)
  {
    var rows = new List<SampleImportRow>();
    var inRows = false;

    foreach (var rawLine in resultText.Split('\n')) {
      var line = rawLine.Trim();
      if (line == "$$SOE") {
        inRows = true;
        continue;
      }

      if (line == "$$EOE") {
        break;
      }

      if (!inRows || string.IsNullOrWhiteSpace(line)) {
        continue;
      }

      var columns = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (columns.Length < 8) {
        throw new InvalidOperationException($"Unexpected Horizons CSV row for '{slug}': {line}");
      }

      var parsedTime = DateTime.ParseExact(columns[1], "A.D. yyyy-MMM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture, DateTimeStyles.None);
      rows.Add(new SampleImportRow(
        bodyId,
        DateTime.SpecifyKind(parsedTime, DateTimeKind.Utc),
        double.Parse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(columns[4], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(columns[5], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(columns[6], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(columns[7], NumberStyles.Float, CultureInfo.InvariantCulture),
        "Ecliptic J2000 / Solar System Barycenter",
        "JPL Horizons API"));
    }

    return rows;
  }

  private static string BuildHorizonsVectorsUri(string command, DateTime startUtc, DateTime endUtc, TimeSpan step)
  {
    var quotedCommand = Uri.EscapeDataString($"'{command}'");
    var quotedStart = Uri.EscapeDataString($"'{startUtc:yyyy-MM-dd HH:mm}'");
    var quotedEnd = Uri.EscapeDataString($"'{endUtc:yyyy-MM-dd HH:mm}'");
    var stepHours = Math.Max(1, (int)Math.Round(step.TotalHours, MidpointRounding.AwayFromZero));
    var quotedStep = Uri.EscapeDataString($"'{stepHours} h'");

    return $"{HorizonsApiBase}?format=json&COMMAND={quotedCommand}&OBJ_DATA='NO'&MAKE_EPHEM='YES'&EPHEM_TYPE='VECTORS'&CENTER='500@0'&REF_PLANE='ECLIPTIC'&REF_SYSTEM='ICRF'&OUT_UNITS='AU-D'&TIME_TYPE='UT'&START_TIME={quotedStart}&STOP_TIME={quotedEnd}&STEP_SIZE={quotedStep}&VEC_TABLE='2'&CSV_FORMAT='YES'";
  }

  private static async Task<int> DeleteExistingSamplesAsync(SqlConnection connection, SqlTransaction transaction, IEnumerable<int> bodyIds, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
  {
	var bodyIdList = string.Join(',', bodyIds.Distinct().OrderBy(bodyId => bodyId));
	if (string.IsNullOrWhiteSpace(bodyIdList)) {
		return 0;
	}

    const string sql = @"
DELETE FROM dbo.EphemerisSamples
WHERE BodyId IN (
	SELECT TRY_CAST(value AS INT)
	FROM string_split(@bodyIds, ',')
)
	AND SampleTimeUtc >= @startUtc
	AND SampleTimeUtc <= @endUtc;";

    await using var command = new SqlCommand(sql, connection, transaction);
	command.Parameters.AddWithValue("@bodyIds", bodyIdList);
    command.Parameters.AddWithValue("@startUtc", startUtc);
    command.Parameters.AddWithValue("@endUtc", endUtc);
    return await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<int> InsertSamplesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<SampleImportRow> samples, CancellationToken cancellationToken)
  {
    if (samples.Count == 0) {
      return 0;
    }

    var table = CreateSampleDataTable();
    foreach (var sample in samples) {
      table.Rows.Add(
        sample.BodyId,
        sample.SampleTimeUtc,
        sample.X,
        sample.Y,
        sample.Z,
        sample.Vx,
        sample.Vy,
        sample.Vz,
        sample.Frame,
        sample.Source);
    }

    using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
    {
      DestinationTableName = "dbo.EphemerisSamples",
      BulkCopyTimeout = 0
    };

    bulkCopy.ColumnMappings.Add("BodyId", "BodyId");
    bulkCopy.ColumnMappings.Add("SampleTimeUtc", "SampleTimeUtc");
    bulkCopy.ColumnMappings.Add("X_AU", "X_AU");
    bulkCopy.ColumnMappings.Add("Y_AU", "Y_AU");
    bulkCopy.ColumnMappings.Add("Z_AU", "Z_AU");
    bulkCopy.ColumnMappings.Add("VX_AUPerDay", "VX_AUPerDay");
    bulkCopy.ColumnMappings.Add("VY_AUPerDay", "VY_AUPerDay");
    bulkCopy.ColumnMappings.Add("VZ_AUPerDay", "VZ_AUPerDay");
    bulkCopy.ColumnMappings.Add("Frame", "Frame");
    bulkCopy.ColumnMappings.Add("Source", "Source");

    await bulkCopy.WriteToServerAsync(table, cancellationToken);
    return table.Rows.Count;
  }

  private static DataTable CreateSampleDataTable()
  {
    var table = new DataTable();
    table.Columns.Add("BodyId", typeof(int));
    table.Columns.Add("SampleTimeUtc", typeof(DateTime));
    table.Columns.Add("X_AU", typeof(double));
    table.Columns.Add("Y_AU", typeof(double));
    table.Columns.Add("Z_AU", typeof(double));
    table.Columns.Add("VX_AUPerDay", typeof(double));
    table.Columns.Add("VY_AUPerDay", typeof(double));
    table.Columns.Add("VZ_AUPerDay", typeof(double));
    table.Columns.Add("Frame", typeof(string));
    table.Columns.Add("Source", typeof(string));
    return table;
  }

  private sealed record SampleImportRow(
    int BodyId,
    DateTime SampleTimeUtc,
    double X,
    double Y,
    double Z,
    double Vx,
    double Vy,
    double Vz,
    string Frame,
    string Source);

}