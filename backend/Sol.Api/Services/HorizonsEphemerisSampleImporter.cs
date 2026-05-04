using Microsoft.Data.SqlClient;
using Sol.Api.Models;
using System.Data;
using System.Globalization;

namespace Sol.Api.Services;

public sealed partial class HorizonsEphemerisSampleImporter(
  HttpClient httpClient,
  ISqlWriteConnectionFactory connectionFactory) : IEphemerisSampleImporter
{
  private const string HorizonsApiBase = "https://ssd.jpl.nasa.gov/api/horizons.api";

  private readonly HttpClient _httpClient = httpClient;
  private readonly ISqlWriteConnectionFactory _connectionFactory = connectionFactory;

  // -------------------------------------------------------------------------
  // Public interface
  // -------------------------------------------------------------------------

  public async Task<EphemerisSampleImportResult> ImportAsync(
      double? hMax, DateTime? startUtc, DateTime? endUtc, TimeSpan? sampleRateOverride, CancellationToken cancellationToken)
  {
    if (sampleRateOverride is not null && sampleRateOverride <= TimeSpan.Zero)
      throw new ArgumentOutOfRangeException(nameof(sampleRateOverride));

    double? batchStartJd = startUtc.HasValue
      ? JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc.Value, DateTimeKind.Utc)) : null;
    double? batchEndJd = endUtc.HasValue
      ? JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc.Value, DateTimeKind.Utc))   : null;

    var bodies = await LoadBodiesForEphemerisAsync(hMax, cancellationToken);
    Console.WriteLine($"Importing ephemeris for {bodies.Count:N0} bodies (hMax={hMax?.ToString() ?? "none"}, parallelism=5).");

    int totalBodies = 0, totalSamples = 0, completed = 0;
    var step = sampleRateOverride ?? TimeSpan.FromDays(1);

    await Parallel.ForEachAsync(
      bodies,
      new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancellationToken },
      async ((int BodyId, string Slug, string JplId, double MinJd, double MaxJd) body, CancellationToken ct) =>
      {
        var (bodyId, slug, jplId, minJd, maxJd) = body;

        // Clip optional batch range to what Horizons covers for this body.
        var effectiveStart = batchStartJd.HasValue ? Math.Max(batchStartJd.Value, minJd) : minJd;
        var effectiveEnd   = batchEndJd.HasValue   ? Math.Min(batchEndJd.Value,   maxJd) : maxJd;
        if (effectiveStart >= effectiveEnd) {
          Interlocked.Increment(ref completed);
          return;
        }

        try {
          await using var conn = _connectionFactory.CreateConnection();
          await conn.OpenAsync(ct);

          int inserted = 0;
          foreach (var window in EphemerisImportSourcePolicy.GetWindowsForTarget(slug, effectiveStart, effectiveEnd, sampleRateOverride)) {
            int windowInserted = await ImportBodyChunksAsync(conn, bodyId, slug, jplId, window.StartJd, window.EndJd, window.Step, ct);
            if (windowInserted > 0 && inserted == 0) {
              await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
              await MarkHasEphemerisAsync(conn, tx, bodyId, ct);
              await tx.CommitAsync(ct);
            }
            inserted += windowInserted;
          }

          // Mark body as complete if all chunks in its full stored range are now logged.
          if (await IsRangeFullyLoggedAsync(conn, bodyId, minJd, maxJd, step, ct))
            await SetCompletedEphemerisAsync(conn, bodyId, ct);

          var n = Interlocked.Increment(ref completed);
          if (inserted > 0) {
            Interlocked.Increment(ref totalBodies);
            Interlocked.Add(ref totalSamples, inserted);
            Console.WriteLine($"  [{n}/{bodies.Count}] {slug}: +{inserted:N0} samples");
          }
          else if (n % 50 == 0) {
            Console.WriteLine($"  [{n}/{bodies.Count}] {n:N0} checked");
          }
        }
        catch (Exception ex) {
          Interlocked.Increment(ref completed);
          Console.WriteLine($"  ERROR {slug}: {ex.Message}");
        }
      });

    return new EphemerisSampleImportResult(totalBodies, totalSamples, 0);
  }

  // -------------------------------------------------------------------------
  // Core chunk-import loop (shared by both import paths)
  // -------------------------------------------------------------------------

  // Iterates chunks within [startJd, endJd], skipping any already logged.
  // Fetches each missing chunk from Horizons, inserts new samples (WHERE NOT
  // EXISTS), and writes a log entry regardless of whether data was returned.
  // HTTP errors are not logged so they are retried on the next run.
  private async Task<int> ImportBodyChunksAsync(
      SqlConnection conn,
      int bodyId, string slug, string horizonsCommand,
      double startJd, double endJd, TimeSpan step,
      CancellationToken ct)
  {
    var loggedChunks = await LoadLoggedChunksAsync(conn, bodyId, startJd, endJd, ct);
    int totalInserted = 0;

    foreach (var (winStart, winEnd) in ChunkRange(startJd, endJd, step)) {
      if (loggedChunks.Contains((winStart, winEnd))) continue;

      var requestUri = BuildHorizonsVectorsUri(horizonsCommand, winStart, winEnd, step);
      using var response = await _httpClient.GetAsync(requestUri, ct);

      if (!response.IsSuccessStatusCode)
        continue; // transient error — do not log, allow retry

      await using var stream = await response.Content.ReadAsStreamAsync(ct);
      using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

      int inserted = 0;
      if (doc.RootElement.TryGetProperty("result", out var resultEl)) {
        var resultText = resultEl.GetString();
        if (!string.IsNullOrEmpty(resultText) && resultText.Contains("$$SOE")) {
          var samples = ParseHorizonsVectorCsv(bodyId, resultText, slug);
          if (samples.Count > 0)
            inserted = await InsertSamplesAsync(conn, samples, ct);
        }
      }

      // Log every attempted chunk within the valid range (0 samples = Horizons
      // confirmed no data here, so we won't retry).
      await LogChunkAsync(conn, bodyId, winStart, winEnd, inserted, ct);
      totalInserted += inserted;

      if (winEnd < endJd)
        await Task.Delay(150, ct);
    }

    return totalInserted;
  }

  // -------------------------------------------------------------------------
  // Horizons API helpers
  // -------------------------------------------------------------------------

  private static IEnumerable<(double Start, double End)> ChunkRange(double startJd, double endJd, TimeSpan step)
  {
    const int maxLinesPerRequest = 18250; // ~50 years at 1-day step
    double windowDays = maxLinesPerRequest * step.TotalDays;
    double windowStart = startJd;
    while (windowStart < endJd) {
      double windowEnd = Math.Min(windowStart + windowDays, endJd);
      yield return (windowStart, windowEnd);
      windowStart = windowEnd + step.TotalDays;
    }
  }

  private static string BuildHorizonsVectorsUri(string command, double startJd, double endJd, TimeSpan step)
  {
    var quotedCommand = Uri.EscapeDataString($"'{command}'");
    var quotedStart   = Uri.EscapeDataString($"'{JulianDateConverter.ToHorizonsDateString(startJd)}'");
    var quotedEnd     = Uri.EscapeDataString($"'{JulianDateConverter.ToHorizonsDateString(endJd)}'");
    var stepHours     = Math.Max(1, (int)Math.Round(step.TotalHours, MidpointRounding.AwayFromZero));
    var quotedStep    = Uri.EscapeDataString($"'{stepHours} h'");

    return $"{HorizonsApiBase}?format=json&COMMAND={quotedCommand}&OBJ_DATA='NO'&MAKE_EPHEM='YES'" +
           $"&EPHEM_TYPE='VECTORS'&CENTER='500@0'&REF_PLANE='ECLIPTIC'&REF_SYSTEM='ICRF'" +
           $"&OUT_UNITS='AU-D'&TIME_TYPE='UT'&START_TIME={quotedStart}&STOP_TIME={quotedEnd}" +
           $"&STEP_SIZE={quotedStep}&VEC_TABLE='2'&CSV_FORMAT='YES'";
  }

  private static IReadOnlyList<SampleImportRow> ParseHorizonsVectorCsv(int bodyId, string resultText, string slug)
  {
    var rows = new List<SampleImportRow>();
    var inRows = false;

    foreach (var rawLine in resultText.Split('\n')) {
      var line = rawLine.Trim();
      if (line == "$$SOE") { inRows = true;  continue; }
      if (line == "$$EOE") { break; }
      if (!inRows || string.IsNullOrWhiteSpace(line)) continue;

      var cols = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (cols.Length < 8)
        throw new InvalidOperationException($"Unexpected Horizons CSV row for '{slug}': {line}");

      rows.Add(new SampleImportRow(
        bodyId,
        JulianDateConverter.ParseHorizonsTimestamp(cols[1]),
        double.Parse(cols[2], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(cols[5], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(cols[6], NumberStyles.Float, CultureInfo.InvariantCulture),
        double.Parse(cols[7], NumberStyles.Float, CultureInfo.InvariantCulture),
        "Ecliptic J2000 / Solar System Barycenter",
        "JPL Horizons API"));
    }

    return rows;
  }

  // -------------------------------------------------------------------------
  // Database helpers
  // -------------------------------------------------------------------------

  private async Task<List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)>>
      LoadBodiesForEphemerisAsync(double? hMax, CancellationToken ct)
  {
    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);

    // Include bodies with no H magnitude (authoritative bodies: planets, comets, probes)
    // and bodies bright enough to pass the h_max cutoff.
    // Require JplHorizonsId and stored epoch range so we know how to query Horizons.
    var sql = @"
SELECT BodyId, Slug, JplHorizonsId, EphemerisMinJD, EphemerisMaxJD
FROM dbo.Bodies
WHERE IsActive = 1
  AND CompletedEphemeris = 0
  AND JplHorizonsId IS NOT NULL
  AND EphemerisMinJD IS NOT NULL
  AND EphemerisMaxJD IS NOT NULL
  AND (H_AbsMag IS NULL" + (hMax.HasValue ? " OR H_AbsMag <= @hMax" : "") + @")
ORDER BY H_AbsMag ASC, Slug;";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    if (hMax.HasValue) cmd.Parameters.AddWithValue("@hMax", hMax.Value);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var list = new List<(int, string, string, double, double)>();
    while (await reader.ReadAsync(ct))
      list.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4)));
    return list;
  }

  private static async Task<HashSet<(double, double)>> LoadLoggedChunksAsync(
      SqlConnection conn, int bodyId, double startJd, double endJd, CancellationToken ct)
  {
    const string sql = @"
SELECT StartJd, EndJd FROM dbo.EphemerisImportLog
WHERE BodyId = @bodyId AND StartJd >= @startJd AND EndJd <= @endJd;";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId",  bodyId);
    cmd.Parameters.AddWithValue("@startJd", startJd);
    cmd.Parameters.AddWithValue("@endJd",   endJd);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var result = new HashSet<(double, double)>();
    while (await reader.ReadAsync(ct))
      result.Add((reader.GetDouble(0), reader.GetDouble(1)));
    return result;
  }

  private static async Task LogChunkAsync(
      SqlConnection conn, int bodyId, double startJd, double endJd, int sampleCount, CancellationToken ct)
  {
    // IF NOT EXISTS prevents duplicate log entries (e.g. from parallel runs).
    const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.EphemerisImportLog WHERE BodyId = @bodyId AND StartJd = @startJd AND EndJd = @endJd)
    INSERT INTO dbo.EphemerisImportLog (BodyId, StartJd, EndJd, SampleCount)
    VALUES (@bodyId, @startJd, @endJd, @sampleCount);";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId",      bodyId);
    cmd.Parameters.AddWithValue("@startJd",     startJd);
    cmd.Parameters.AddWithValue("@endJd",       endJd);
    cmd.Parameters.AddWithValue("@sampleCount", sampleCount);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<bool> IsRangeFullyLoggedAsync(
      SqlConnection conn, int bodyId, double minJd, double maxJd, TimeSpan step, CancellationToken ct)
  {
    int expectedCount = ChunkRange(minJd, maxJd, step).Count();

    const string sql = @"
SELECT COUNT(*) FROM dbo.EphemerisImportLog
WHERE BodyId = @bodyId AND StartJd >= @minJd AND EndJd <= @maxJd;";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId", bodyId);
    cmd.Parameters.AddWithValue("@minJd",  minJd);
    cmd.Parameters.AddWithValue("@maxJd",  maxJd);
    int loggedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    return loggedCount >= expectedCount;
  }

  private static async Task SetCompletedEphemerisAsync(
      SqlConnection conn, int bodyId, CancellationToken ct)
  {
    const string sql = "UPDATE dbo.Bodies SET CompletedEphemeris = 1, UpdatedUtc = SYSUTCDATETIME() WHERE BodyId = @id;";
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@id", bodyId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task MarkHasEphemerisAsync(
      SqlConnection conn, SqlTransaction tx, int bodyId, CancellationToken ct)
  {
    const string sql = "UPDATE dbo.Bodies SET HasEphemeris = 1, UpdatedUtc = SYSUTCDATETIME() WHERE BodyId = @id;";
    await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@id", bodyId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  // Bulk-loads samples into a connection-local staging table then inserts rows
  // that don't already exist in dbo.EphemerisSamples (by BodyId + SampleJd).
  // Manages its own transaction; the staging table is dropped implicitly when
  // the connection closes.
  private static async Task<int> InsertSamplesAsync(
      SqlConnection conn, IReadOnlyList<SampleImportRow> samples, CancellationToken ct)
  {
    if (samples.Count == 0) return 0;

    // Drop and recreate the staging table (idempotent within this connection).
    const string createStaging = @"
IF OBJECT_ID('tempdb..#EphemerisStaging') IS NOT NULL DROP TABLE #EphemerisStaging;
CREATE TABLE #EphemerisStaging (
    BodyId      INT           NOT NULL,
    SampleJd    FLOAT         NOT NULL,
    X_AU        FLOAT         NOT NULL,
    Y_AU        FLOAT         NOT NULL,
    Z_AU        FLOAT         NOT NULL,
    VX_AUPerDay FLOAT         NOT NULL,
    VY_AUPerDay FLOAT         NOT NULL,
    VZ_AUPerDay FLOAT         NOT NULL,
    Frame       NVARCHAR(256) COLLATE DATABASE_DEFAULT NULL,
    Source      NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL
);";
    await using (var createCmd = new SqlCommand(createStaging, conn))
      await createCmd.ExecuteNonQueryAsync(ct);

    // Bulk-load into the staging table (outside any transaction).
    var table = CreateSampleDataTable();
    foreach (var s in samples)
      table.Rows.Add(s.BodyId, s.SampleJd, s.X, s.Y, s.Z, s.Vx, s.Vy, s.Vz, s.Frame, s.Source);

    using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null) {
      DestinationTableName = "#EphemerisStaging",
      BulkCopyTimeout = 0
    }) {
      bulk.ColumnMappings.Add("BodyId",      "BodyId");
      bulk.ColumnMappings.Add("SampleJd",    "SampleJd");
      bulk.ColumnMappings.Add("X_AU",        "X_AU");
      bulk.ColumnMappings.Add("Y_AU",        "Y_AU");
      bulk.ColumnMappings.Add("Z_AU",        "Z_AU");
      bulk.ColumnMappings.Add("VX_AUPerDay", "VX_AUPerDay");
      bulk.ColumnMappings.Add("VY_AUPerDay", "VY_AUPerDay");
      bulk.ColumnMappings.Add("VZ_AUPerDay", "VZ_AUPerDay");
      bulk.ColumnMappings.Add("Frame",       "Frame");
      bulk.ColumnMappings.Add("Source",      "Source");
      await bulk.WriteToServerAsync(table, ct);
    }

    // Insert only rows that don't already exist. MERGE uses a join plan against
    // the (BodyId, SampleJd) index — much faster than a correlated WHERE NOT EXISTS
    // as the table grows to millions of rows.
    const string insertSql = @"
MERGE dbo.EphemerisSamples AS tgt
USING #EphemerisStaging AS src ON tgt.BodyId = src.BodyId AND tgt.SampleJd = src.SampleJd
WHEN NOT MATCHED BY TARGET THEN INSERT (
    BodyId, SampleJd, X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source
) VALUES (
    src.BodyId, src.SampleJd, src.X_AU, src.Y_AU, src.Z_AU,
    src.VX_AUPerDay, src.VY_AUPerDay, src.VZ_AUPerDay, src.Frame, src.Source
);";

    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
    await using var insertCmd = new SqlCommand(insertSql, conn, tx) { CommandTimeout = 0 };
    int inserted = await insertCmd.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return inserted;
  }

  private static DataTable CreateSampleDataTable()
  {
    var table = new DataTable();
    table.Columns.Add("BodyId",      typeof(int));
    table.Columns.Add("SampleJd",    typeof(double));
    table.Columns.Add("X_AU",        typeof(double));
    table.Columns.Add("Y_AU",        typeof(double));
    table.Columns.Add("Z_AU",        typeof(double));
    table.Columns.Add("VX_AUPerDay", typeof(double));
    table.Columns.Add("VY_AUPerDay", typeof(double));
    table.Columns.Add("VZ_AUPerDay", typeof(double));
    table.Columns.Add("Frame",       typeof(string));
    table.Columns.Add("Source",      typeof(string));
    return table;
  }

  private sealed record SampleImportRow(
    int BodyId, double SampleJd,
    double X, double Y, double Z,
    double Vx, double Vy, double Vz,
    string Frame, string Source);
}
