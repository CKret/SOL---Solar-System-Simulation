using Microsoft.Data.SqlClient;
using Sol.Api.Models;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Sol.Api.Services;

public sealed partial class HorizonsEphemerisSampleImporter(
  HttpClient httpClient,
  ISqlWriteConnectionFactory connectionFactory) : IEphemerisSampleImporter
{
  private const string HorizonsApiBase = "https://ssd.jpl.nasa.gov/api/horizons.api";
  private const string EphemerisFrame = "Ecliptic J2000 / Solar System Barycenter";
  private const string EphemerisSource = "JPL Horizons API";

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
    Console.WriteLine($"Importing ephemeris for {bodies.Count:N0} bodies (hMax={hMax?.ToString() ?? "none"}, parallelism=2).");

    int totalBodies = 0, totalSamples = 0, completed = 0;
    var step = sampleRateOverride ?? TimeSpan.FromDays(1);

    await Parallel.ForEachAsync(
      bodies,
      new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
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

        Console.WriteLine($"  → {slug}");
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

    // Reset newly-completed bodies that still have zero-sample chunks so the
    // DB count is accurate immediately after the run, and they get retried next pass.
    var resetCount = await ResetBodiesWithZeroChunksAsync(hMax, cancellationToken);
    if (resetCount > 0)
      Console.WriteLine($"Reset {resetCount} bodies with zero-sample chunks (will retry next run).");

    var remaining = await CountIncompleteBodiesAsync(hMax, cancellationToken);
    Console.WriteLine(remaining > 0
      ? $"{remaining} bodies still incomplete — run again to continue."
      : "All target bodies complete.");

    return new EphemerisSampleImportResult(totalBodies, totalSamples, 0);
  }

  // -------------------------------------------------------------------------
  // Core chunk-import loop (shared by both import paths)
  // -------------------------------------------------------------------------

  // Iterates chunks within [startJd, endJd], skipping any already logged.
  // Fetches each missing chunk from Horizons, inserts new samples (WHERE NOT
  // EXISTS), and writes a log entry regardless of whether data was returned.
  // HTTP errors are not logged so they are retried on the next run.
  private const int BoundaryMaxShrinkDays = 10;

  private async Task<int> ImportBodyChunksAsync(
      SqlConnection conn,
      int bodyId, string slug, string horizonsCommand,
      double startJd, double endJd, TimeSpan step,
      CancellationToken ct)
  {
    var loggedChunks = await LoadLoggedChunksAsync(conn, bodyId, startJd, endJd, ct);
    int totalInserted = 0;

    var allChunks  = ChunkRange(startJd, endJd, step).ToList();
    int totalChunks = allChunks.Count;
    int chunkIndex  = 0;

    foreach (var (winStart, winEnd) in allChunks) {
      chunkIndex++;
      if (loggedChunks.Contains((winStart, winEnd))) continue;

      Console.WriteLine($"    {slug} {chunkIndex}/{totalChunks} JD{winStart:F0}..JD{winEnd:F0}");
      await Task.Delay(1000, ct); // avoid JPL Horizons rate limiting
      var fetch = await FetchAndInsertChunkAsync(conn, bodyId, slug, horizonsCommand, winStart, winEnd, step, ct);
      if (fetch.Inserted < 0) continue; // transient HTTP error — do not log, allow retry

      // Use effective boundaries from the fetch (may differ from winStart/winEnd if
      // a Horizons boundary error caused an internal adjustment).
      var logStart = fetch.EffStart;
      var logEnd   = fetch.EffEnd;
      var inserted = fetch.Inserted;

      // If a boundary chunk returned 0 samples, JPL's catalog date may be 1+ days
      // outside what Horizons actually serves. Shrink the boundary edge 1 day at a
      // time until data is found or we reach BoundaryMaxShrinkDays.
      if (inserted == 0) {
        var isFirstChunk = Math.Abs(winStart - startJd) < 0.5;
        var isLastChunk  = Math.Abs(winEnd   - endJd)   < 0.5;

        if (isFirstChunk || isLastChunk) {
          int shrink = 1;
          while (shrink <= BoundaryMaxShrinkDays) {
            var retryStart = isFirstChunk ? winStart + shrink : winStart;
            var retryEnd   = isLastChunk  ? winEnd   - shrink : winEnd;
            if (retryStart >= retryEnd) break;

            await Task.Delay(150, ct);
            var rf = await FetchAndInsertChunkAsync(conn, bodyId, slug, horizonsCommand, retryStart, retryEnd, step, ct);

            if (rf.Inserted < 0) { await Task.Delay(500, ct); continue; } // HTTP error — retry same shrink level
            shrink++; // only advance past a confirmed result (0 = no data, >0 = data)

            if (rf.Inserted == 0) continue; // Horizons confirmed no data — shrink more

            // Found data — record the effective boundaries and update the stored range.
            inserted = rf.Inserted;
            logStart = isFirstChunk ? retryStart : winStart;
            logEnd   = isLastChunk  ? retryEnd   : winEnd;
            await UpdateBodyEphemerisBoundaryAsync(conn, bodyId,
              isFirstChunk ? retryStart : null,
              isLastChunk  ? retryEnd   : null, ct);
            break;
          }
          if (inserted < 0) inserted = 0; // treat persistent HTTP errors as 0 for logging
        }
      }

      // If the retry found a shorter range, delete the stale original entry before
      // logging with the actual boundaries so the log key stays consistent.
      if (logStart != winStart || logEnd != winEnd)
        await DeleteLogChunkAsync(conn, bodyId, winStart, winEnd, ct);
      await LogChunkAsync(conn, bodyId, logStart, logEnd, inserted, ct);
      totalInserted += inserted;

      if (winEnd < endJd)
        await Task.Delay(150, ct);
    }

    return totalInserted;
  }

  private async Task<ChunkFetchResult> FetchAndInsertChunkAsync(
      SqlConnection conn,
      int bodyId, string slug, string horizonsCommand,
      double winStart, double winEnd, TimeSpan step,
      CancellationToken ct)
  {
    var requestUri = BuildHorizonsVectorsUri(horizonsCommand, winStart, winEnd, step);
    using var response = await _httpClient.GetAsync(requestUri, ct);

    if (!response.IsSuccessStatusCode)
      return new(-1, winStart, winEnd); // transient error — caller should not log, allow retry

    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

    // Horizons returns an "error" field when the request falls outside the valid
    // ephemeris range (e.g. Pluto before 1800 or after 2200). Parse the actual
    // boundary date, update the stored range, and retry within the valid window.
    if (doc.RootElement.TryGetProperty("error", out var errEl)) {
      var errText = errEl.GetString() ?? "";
      var adjStart = winStart;
      var adjEnd   = winEnd;

      var priorM = System.Text.RegularExpressions.Regex.Match(errText,
        @"prior to A\.D\.\s+(\d+)-([A-Z]{3})-(\d+)\s+([\d:.]+)");
      if (priorM.Success) {
        adjStart = ParseHorizonsErrorDateToJd(priorM);
        await UpdateBodyEphemerisBoundaryAsync(conn, bodyId, adjStart, null, ct);
        await DeleteOutOfRangeLogChunksAsync(conn, bodyId, adjStart, null, ct);
      }

      var afterM = System.Text.RegularExpressions.Regex.Match(errText,
        @"after A\.D\.\s+(\d+)-([A-Z]{3})-(\d+)\s+([\d:.]+)");
      if (afterM.Success) {
        adjEnd = ParseHorizonsErrorDateToJd(afterM);
        await UpdateBodyEphemerisBoundaryAsync(conn, bodyId, null, adjEnd, ct);
        await DeleteOutOfRangeLogChunksAsync(conn, bodyId, null, adjEnd, ct);
      }

      if (adjStart < adjEnd && (adjStart > winStart || adjEnd < winEnd)) {
        Console.WriteLine($"    {slug} boundary adjusted JD{adjStart:F0}..JD{adjEnd:F0}");
        return await FetchAndInsertChunkAsync(conn, bodyId, slug, horizonsCommand, adjStart, adjEnd, step, ct);
      }
      return new(0, winStart, winEnd);
    }

    if (!doc.RootElement.TryGetProperty("result", out var resultEl)) return new(0, winStart, winEnd);
    var resultText = resultEl.GetString();
    if (string.IsNullOrEmpty(resultText)) return new(0, winStart, winEnd);

    // Horizons returns a disambiguation list when DES= matches multiple apparition
    // solutions (common for periodic comets). Pick the solution whose epoch year is
    // closest to the chunk midpoint and retry with that specific record number.
    if (resultText.Contains("To SELECT, enter record #")) {
      var record = PickBestApparitionRecord(resultText, (winStart + winEnd) / 2.0);
      if (record == null) return new(0, winStart, winEnd);
      Console.WriteLine($"    {slug} → apparition record {record}");
      return await FetchAndInsertChunkAsync(conn, bodyId, slug, $"{record};", winStart, winEnd, step, ct);
    }

    if (resultText.Contains("$$SOE")) {
      var samples = ParseHorizonsVectorCsv(bodyId, resultText, slug);
      if (samples.Count > 0) {
        await InsertSamplesAsync(conn, samples, ct);
        // Log samples.Count (what Horizons returned), not the insert delta, so the
        // log is accurate regardless of whether data already existed in the DB.
        return new(samples.Count, winStart, winEnd);
      }
    }

    return new(0, winStart, winEnd);
  }

  private readonly record struct ChunkFetchResult(int Inserted, double EffStart, double EffEnd);

  // Parses a date from a Horizons boundary error message such as:
  //   "prior to A.D. 1800-JAN-02 23:59:41.3795 UT"
  //   "after A.D. 2199-DEC-28 23:58:50.8163 UT"
  private static double ParseHorizonsErrorDateToJd(System.Text.RegularExpressions.Match m)
  {
    var year  = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    var month = m.Groups[2].Value switch {
      "JAN"=>1,"FEB"=>2,"MAR"=>3,"APR"=>4,"MAY"=>5,"JUN"=>6,
      "JUL"=>7,"AUG"=>8,"SEP"=>9,"OCT"=>10,"NOV"=>11,"DEC"=>12, _=>1
    };
    var day   = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    var parts = m.Groups[4].Value.Split(':');
    var hour  = parts.Length > 0 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
    var min   = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
    var sec   = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
    return JulianDateConverter.FromCalendar(year, month, day + (hour * 3600 + min * 60 + sec) / 86400.0);
  }

  // Parses Horizons disambiguation table and returns the record number whose
  // epoch year is closest to the given Julian Date (converted to calendar year).
  private static string? PickBestApparitionRecord(string resultText, double midJd)
  {
    var midYear = 2000.0 + (midJd - 2451545.0) / 365.25;
    string? best = null;
    double bestDiff = double.MaxValue;

    foreach (var line in resultText.Split('\n')) {
      // Lines look like: "    90000702    2015    1000012    67P    Churyumov-Gerasimenko"
      var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\d{5,})\s+(\d{4})\s+");
      if (!m.Success || !int.TryParse(m.Groups[2].Value, out var year)) continue;
      var diff = Math.Abs(year - midYear);
      if (diff < bestDiff) { bestDiff = diff; best = m.Groups[1].Value; }
    }

    return best;
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
    var quotedStart   = Uri.EscapeDataString($"'JD {startJd}'");
    var quotedEnd     = Uri.EscapeDataString($"'JD {endJd}'");
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
        double.Parse(cols[7], NumberStyles.Float, CultureInfo.InvariantCulture)));
    }

    return rows;
  }

  // -------------------------------------------------------------------------
  // Retry zero-sample chunks
  // -------------------------------------------------------------------------

  // Finds all EphemerisImportLog entries where SampleCount = 0 and retries each,
  // incrementally shrinking boundary edges by 1 day at a time up to maxShrinkDays.
  // Only the side touching EphemerisMinJD/MaxJD is shrunk — middle chunks are
  // retried unchanged. Stops as soon as samples are returned for each chunk.
  public async Task<int> RetryZeroSamplesAsync(double maxShrinkDays, CancellationToken ct)
  {
    var zeros = await LoadZeroSampleChunksAsync(ct);
    Console.WriteLine($"Retrying {zeros.Count} zero-sample chunks (shrink up to {maxShrinkDays} day(s) on boundary edges).");
    if (zeros.Count == 0) return 0;

    // Reset CompletedEphemeris for all affected bodies so import-samples can
    // re-evaluate them after boundaries are corrected.
    var affectedIds = zeros.Select(z => z.BodyId).Distinct().ToList();
    await ResetCompletedEphemerisAsync(affectedIds, ct);
    Console.WriteLine($"Reset CompletedEphemeris=0 for {affectedIds.Count} affected bodies.");

    int totalInserted = 0;

    foreach (var (bodyId, slug, jplId, startJd, endJd, ephMinJd, ephMaxJd) in zeros) {
      var isFirstChunk = Math.Abs(startJd - ephMinJd) < 0.5;
      var isLastChunk  = Math.Abs(endJd   - ephMaxJd) < 0.5;

      int inserted = 0;
      var logStart = startJd;
      var logEnd   = endJd;

      int shrink = 1;
      while (shrink <= (int)maxShrinkDays) {
        var retryStart = isFirstChunk ? startJd + shrink : startJd;
        var retryEnd   = isLastChunk  ? endJd   - shrink : endJd;
        if (retryStart >= retryEnd) break;

        var requestUri = BuildHorizonsVectorsUri(jplId, retryStart, retryEnd, TimeSpan.FromDays(1));
        using var response = await _httpClient.GetAsync(requestUri, ct);
        if (!response.IsSuccessStatusCode) { await Task.Delay(500, ct); continue; } // HTTP error — retry same shrink
        shrink++; // advance only on confirmed result

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("result", out var resultEl)) {
          var resultText = resultEl.GetString();
          if (!string.IsNullOrEmpty(resultText) && resultText.Contains("$$SOE")) {
            var samples = ParseHorizonsVectorCsv(bodyId, resultText, slug);
            if (samples.Count > 0) {
              await using var conn = _connectionFactory.CreateConnection();
              await conn.OpenAsync(ct);
              inserted = await InsertSamplesAsync(conn, samples, ct);
              if (inserted > 0) {
                logStart = isFirstChunk ? retryStart : startJd;
                logEnd   = isLastChunk  ? retryEnd   : endJd;
                // Replace the old log entry with one using the actual boundaries.
                await DeleteLogChunkAsync(conn, bodyId, startJd, endJd, ct);
                await LogChunkAsync(conn, bodyId, logStart, logEnd, inserted, ct);
                await UpdateBodyEphemerisBoundaryAsync(conn, bodyId,
                  isFirstChunk ? retryStart : null,
                  isLastChunk  ? retryEnd   : null, ct);
                Console.WriteLine($"  [{slug}] {startJd:F1}→{endJd:F1}: inserted {inserted} (shrink={(int)(shrink - 1)}d, log={logStart:F1}→{logEnd:F1}).");
                break;
              }
            }
          }
        }

        await Task.Delay(150, ct);
      }

      totalInserted += inserted;
    }

    return totalInserted;
  }

  private async Task<List<(int BodyId, string Slug, string JplId, double StartJd, double EndJd, double EphMinJd, double EphMaxJd)>>
      LoadZeroSampleChunksAsync(CancellationToken ct)
  {
    const string sql = @"
SELECT l.BodyId, b.Slug, b.JplHorizonsId, l.StartJd, l.EndJd, b.EphemerisMinJD, b.EphemerisMaxJD
FROM dbo.EphemerisImportLog l
INNER JOIN dbo.Bodies b ON b.BodyId = l.BodyId
WHERE l.SampleCount = 0
  AND b.JplHorizonsId IS NOT NULL
  AND b.EphemerisMinJD IS NOT NULL
  AND b.EphemerisMaxJD IS NOT NULL
ORDER BY l.BodyId, l.StartJd;";

    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var list = new List<(int, string, string, double, double, double, double)>();
    while (await reader.ReadAsync(ct))
      list.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)));
    return list;
  }

  private static async Task UpdateLogSampleCountAsync(
      SqlConnection conn, int bodyId, double startJd, double endJd, int count, CancellationToken ct)
  {
    const string sql = @"
UPDATE dbo.EphemerisImportLog
SET SampleCount = @count
WHERE BodyId = @bodyId AND StartJd = @startJd AND EndJd = @endJd;";
    await using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@bodyId",  bodyId);
    cmd.Parameters.AddWithValue("@startJd", startJd);
    cmd.Parameters.AddWithValue("@endJd",   endJd);
    cmd.Parameters.AddWithValue("@count",   count);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task<int> CountIncompleteBodiesAsync(double? hMax, CancellationToken ct)
  {
    var hFilter = hMax.HasValue ? "AND (H_AbsMag IS NULL OR H_AbsMag <= @hMax)" : "AND H_AbsMag IS NULL AND Source != 'mpcorb'";
    var sql = $@"
SELECT COUNT(*) FROM dbo.Bodies
WHERE IsActive = 1
  AND CompletedEphemeris = 0
  AND JplHorizonsId IS NOT NULL
  AND EphemerisMinJD IS NOT NULL
  AND EphemerisMaxJD IS NOT NULL
  {hFilter};";
    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    if (hMax.HasValue) cmd.Parameters.AddWithValue("@hMax", hMax.Value);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
  }

  private async Task<int> ResetBodiesWithZeroChunksAsync(double? hMax, CancellationToken ct)
  {
    var hFilter = hMax.HasValue
      ? "AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMax)"
      : "AND b.H_AbsMag IS NULL AND b.Source != 'mpcorb'";
    var sql = $@"
UPDATE b SET CompletedEphemeris = 0, UpdatedUtc = SYSUTCDATETIME()
FROM dbo.Bodies b
WHERE b.IsActive = 1
  AND b.CompletedEphemeris = 1
  AND b.JplHorizonsId IS NOT NULL
  {hFilter}
  AND EXISTS (
    SELECT 1 FROM dbo.EphemerisImportLog l
    WHERE l.BodyId = b.BodyId AND l.SampleCount = 0
  );";

    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    if (hMax.HasValue) cmd.Parameters.AddWithValue("@hMax", hMax.Value);
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task ResetCompletedEphemerisAsync(IReadOnlyList<int> bodyIds, CancellationToken ct)
  {
    var idList = string.Join(",", bodyIds);
    var sql = $"UPDATE dbo.Bodies SET CompletedEphemeris = 0, UpdatedUtc = SYSUTCDATETIME() WHERE IsActive = 1 AND BodyId IN ({idList});";
    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);
    await using var cmd = new SqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task UpdateBodyEphemerisBoundaryAsync(
      SqlConnection conn, int bodyId, double? newMinJd, double? newMaxJd, CancellationToken ct)
  {
    if (newMinJd == null && newMaxJd == null) return;
    var parts = new List<string>();
    if (newMinJd != null) { parts.Add("EphemerisMinJD = @minJd"); parts.Add("EphemerisMinStr = @minStr"); }
    if (newMaxJd != null) { parts.Add("EphemerisMaxJD = @maxJd"); parts.Add("EphemerisMaxStr = @maxStr"); }
    parts.Add("UpdatedUtc = SYSUTCDATETIME()");
    var sql = $"UPDATE dbo.Bodies SET {string.Join(", ", parts)} WHERE BodyId = @bodyId;";
    await using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@bodyId", bodyId);
    if (newMinJd != null) { cmd.Parameters.AddWithValue("@minJd", newMinJd.Value); cmd.Parameters.AddWithValue("@minStr", JdToDisplayStr(newMinJd.Value)); }
    if (newMaxJd != null) { cmd.Parameters.AddWithValue("@maxJd", newMaxJd.Value); cmd.Parameters.AddWithValue("@maxStr", JdToDisplayStr(newMaxJd.Value)); }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static string JdToDisplayStr(double jd)
  {
    var s = JulianDateConverter.ToHorizonsDateString(jd);
    return s.StartsWith("BC ", StringComparison.Ordinal) ? "B.C. " + s[3..] : "A.D. " + s;
  }

  // -------------------------------------------------------------------------
  // Database helpers
  // -------------------------------------------------------------------------

  private async Task<List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)>>
      LoadBodiesForEphemerisAsync(double? hMax, CancellationToken ct)
  {
    await using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(ct);

    // When hMax is null: import only authoritative bodies (H IS NULL, Source != 'mpcorb').
    // When hMax is provided: all null-H bodies (any source) plus any body bright enough (H <= hMax).
    var hFilter = hMax.HasValue
      ? "AND (H_AbsMag IS NULL OR H_AbsMag <= @hMax)"
      : "AND H_AbsMag IS NULL AND Source != 'mpcorb'";
    var sql = $@"
SELECT BodyId, Slug, JplHorizonsId, EphemerisMinJD, EphemerisMaxJD
FROM dbo.Bodies
WHERE IsActive = 1
  AND CompletedEphemeris = 0
  AND JplHorizonsId IS NOT NULL
  AND EphemerisMinJD IS NOT NULL
  AND EphemerisMaxJD IS NOT NULL
  {hFilter}
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
    // Only chunks with SampleCount > 0 are treated as done.
    // SampleCount = 0 entries are treated as gaps so they get retried.
    const string sql = @"
SELECT StartJd, EndJd FROM dbo.EphemerisImportLog
WHERE BodyId = @bodyId AND StartJd >= @startJd AND EndJd <= @endJd AND SampleCount > 0;";

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

  private static async Task DeleteOutOfRangeLogChunksAsync(
      SqlConnection conn, int bodyId, double? newMinJd, double? newMaxJd, CancellationToken ct)
  {
    var conditions = new List<string>();
    if (newMinJd.HasValue) conditions.Add("StartJd < @minJd");
    if (newMaxJd.HasValue) conditions.Add("EndJd > @maxJd");
    if (conditions.Count == 0) return;

    var sql = $@"
DELETE FROM dbo.EphemerisImportLog
WHERE BodyId = @bodyId
  AND ({string.Join(" OR ", conditions)});";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId", bodyId);
    if (newMinJd.HasValue) cmd.Parameters.AddWithValue("@minJd", newMinJd.Value);
    if (newMaxJd.HasValue) cmd.Parameters.AddWithValue("@maxJd", newMaxJd.Value);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task DeleteLogChunkAsync(
      SqlConnection conn, int bodyId, double startJd, double endJd, CancellationToken ct)
  {
    const string sql = "DELETE FROM dbo.EphemerisImportLog WHERE BodyId = @bodyId AND StartJd = @startJd AND EndJd = @endJd;";
    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId",  bodyId);
    cmd.Parameters.AddWithValue("@startJd", startJd);
    cmd.Parameters.AddWithValue("@endJd",   endJd);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task LogChunkAsync(
      SqlConnection conn, int bodyId, double startJd, double endJd, int sampleCount, CancellationToken ct)
  {
    // MERGE upserts: updates SampleCount if the row already exists (e.g. previous zero → now has data),
    // inserts if new. Safe because PK_EphemerisImportLog guarantees at most one target row per key.
    const string sql = @"
MERGE dbo.EphemerisImportLog AS tgt
USING (SELECT @bodyId AS BodyId, @startJd AS StartJd, @endJd AS EndJd) AS src
  ON tgt.BodyId = src.BodyId AND tgt.StartJd = src.StartJd AND tgt.EndJd = src.EndJd
WHEN MATCHED THEN
  UPDATE SET SampleCount = @sampleCount, ImportedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT (BodyId, StartJd, EndJd, SampleCount)
  VALUES (src.BodyId, src.StartJd, src.EndJd, @sampleCount);";

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
    var expectedChunks = ChunkRange(minJd, maxJd, step).ToList();
    if (expectedChunks.Count == 0) return true;

    // Load only successfully-imported chunks (SampleCount > 0).
    // Duplicate entries and zero-sample entries must not inflate the count.
    const string sql = @"
SELECT StartJd, EndJd FROM dbo.EphemerisImportLog
WHERE BodyId = @bodyId AND StartJd >= @minJd AND EndJd <= @maxJd AND SampleCount > 0;";

    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
    cmd.Parameters.AddWithValue("@bodyId", bodyId);
    cmd.Parameters.AddWithValue("@minJd",  minJd);
    cmd.Parameters.AddWithValue("@maxJd",  maxJd);
    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var logged = new List<(double Start, double End)>();
    while (await reader.ReadAsync(ct))
      logged.Add((reader.GetDouble(0), reader.GetDouble(1)));

    // Each expected chunk must be covered by a log entry within ±2 JD on each boundary.
    // This tolerates minor rounding differences from boundary-adjustment retries.
    const double tol = 2.0;
    foreach (var (s, e) in expectedChunks)
    {
      if (!logged.Any(l => Math.Abs(l.Start - s) <= tol && Math.Abs(l.End - e) <= tol))
        return false;
    }
    return true;
  }

  private static async Task SetCompletedEphemerisAsync(
      SqlConnection conn, int bodyId, CancellationToken ct)
  {
    const string sql = "UPDATE dbo.Bodies SET CompletedEphemeris = 1, UpdatedUtc = SYSUTCDATETIME() WHERE IsActive = 1 AND BodyId = @id;";
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
    using var sourceReader = new SampleImportRowDataReader(samples);

    using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null) {
      DestinationTableName = "#EphemerisStaging",
      BulkCopyTimeout = 0,
      EnableStreaming = true,
      BatchSize = 10_000
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
      await bulk.WriteToServerAsync(sourceReader, ct);
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
    try {
      int inserted = await insertCmd.ExecuteNonQueryAsync(ct);
      await tx.CommitAsync(ct);
      return inserted;
    }
    catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2627) {
      // PK violation: data already exists (e.g. chunk overlaps a previously imported run).
      // Roll back and return 0 — caller logs samples.Count so the chunk is marked done.
      await tx.RollbackAsync(ct);
      Console.WriteLine($"    [dup] chunk already fully present, skipping insert.");
      return 0;
    }
  }

  private sealed class SampleImportRowDataReader(IReadOnlyList<SampleImportRow> rows) : DbDataReader
  {
    private static readonly string[] _names = ["BodyId", "SampleJd", "X_AU", "Y_AU", "Z_AU", "VX_AUPerDay", "VY_AUPerDay", "VZ_AUPerDay", "Frame", "Source"];
    private int _index = -1;

    public override int FieldCount => _names.Length;
    public override bool HasRows => rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;

    public override bool Read() => ++_index < rows.Count;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override string GetName(int ordinal) => _names[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_names, name);
    public override Type GetFieldType(int ordinal) => ordinal switch
    {
      0 => typeof(int),
      8 or 9 => typeof(string),
      _ => typeof(double)
    };
    public override object GetValue(int ordinal)
    {
      var r = rows[_index];
      return ordinal switch
      {
        0 => r.BodyId,
        1 => r.SampleJd,
        2 => r.X,
        3 => r.Y,
        4 => r.Z,
        5 => r.Vx,
        6 => r.Vy,
        7 => r.Vz,
        8 => EphemerisFrame,
        9 => EphemerisSource,
        _ => throw new IndexOutOfRangeException()
      };
    }

    public override int GetValues(object[] values)
    {
      var count = Math.Min(values.Length, FieldCount);
      for (var i = 0; i < count; i++)
        values[i] = GetValue(i);
      return count;
    }

    public override bool IsDBNull(int ordinal) => false;
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override Stream GetStream(int ordinal) => throw new NotSupportedException();
    public override TextReader GetTextReader(int ordinal) => throw new NotSupportedException();
  }

  private readonly record struct SampleImportRow(
    int BodyId, double SampleJd,
    double X, double Y, double Z,
    double Vx, double Vy, double Vz);
}
