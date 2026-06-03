using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sol.Api.Data;
using Sol.Api.Models;
using System.Data.Common;
using System.Globalization;

namespace Sol.Api.Services;

public sealed partial class HorizonsEphemerisSampleImporter(
    HttpClient httpClient,
    IDbContextFactory<SolWriteDbContext> dbContextFactory,
    bool debug = false) : IEphemerisSampleImporter
{
    private const string HorizonsApiBase  = "https://ssd.jpl.nasa.gov/api/horizons.api";
    private const string EphemerisFrame   = "Ecliptic J2000 / Solar System Barycenter";
    private const string EphemerisSource  = "JPL Horizons API";

    // Configurable maximum lines per request for chunking
    private static int _maxLinesPerRequest = 40000;
    public static int MaxLinesPerRequest
    {
        get => _maxLinesPerRequest;
        set => _maxLinesPerRequest = value > 0 ? value : 1;
    }


    // Debug flag for timing logs
    private bool _debug = debug;

    public void SetDebug(bool debug)
    {
        _debug = debug;
    }
    // ── Public interface ──────────────────────────────────────────────────────

    public async Task<EphemerisSampleImportResult> ImportAsync(
        double? hMax, DateTime? startUtc, DateTime? endUtc, TimeSpan? sampleRateOverride,
        IReadOnlyList<string>? slugFilter, IReadOnlyList<int>? bodyIdFilter,
        CancellationToken cancellationToken)
    {
        if (sampleRateOverride is not null && sampleRateOverride <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sampleRateOverride));

        double? batchStartJd = startUtc.HasValue
            ? JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc.Value, DateTimeKind.Utc)) : null;
        double? batchEndJd = endUtc.HasValue
            ? JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc.Value, DateTimeKind.Utc)) : null;

        List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)> bodies;
        string filterDesc;
        if (bodyIdFilter is { Count: > 0 }) {
            bodies = await LoadBodiesByIdAsync(bodyIdFilter, cancellationToken);
            filterDesc = $"bodyIds=[{string.Join(",", bodyIdFilter)}]";
        } else if (slugFilter is { Count: > 0 }) {
            bodies = await LoadBodiesBySlugAsync(slugFilter, cancellationToken);
            filterDesc = $"slugs=[{string.Join(",", slugFilter)}]";
        } else {
            bodies = await LoadBodiesForEphemerisAsync(hMax, cancellationToken);
            filterDesc = $"hMax={hMax?.ToString() ?? "none"}";
        }
        Console.WriteLine($"Importing ephemeris for {bodies.Count:N0} bodies ({filterDesc}, parallelism=2x2).");

        int totalBodies = 0, totalSamples = 0, completed = 0;
        var step = sampleRateOverride ?? TimeSpan.FromDays(1);

        await Parallel.ForEachAsync(
            bodies,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async ((int BodyId, string Slug, string JplId, double MinJd, double MaxJd) body, CancellationToken ct) =>
            {
                var (bodyId, slug, jplId, minJd, maxJd) = body;

                var effectiveStart = batchStartJd.HasValue ? Math.Max(batchStartJd.Value, minJd) : minJd;
                var effectiveEnd   = batchEndJd.HasValue   ? Math.Min(batchEndJd.Value,   maxJd) : maxJd;
                if (effectiveStart >= effectiveEnd) {
                    Interlocked.Increment(ref completed);
                    return;
                }

                Console.WriteLine($"  → {slug}");
                try {
                    bool isTargeted = (slugFilter is { Count: > 0 } || bodyIdFilter is { Count: > 0 })
                                      && batchStartJd.HasValue && batchEndJd.HasValue;
                    if (isTargeted) {
                        await using var tCtx = await dbContextFactory.CreateDbContextAsync(ct);
                        await DeleteLogChunksInRangeAsync(tCtx, bodyId, effectiveStart, effectiveEnd, ct);
                    }

                    int inserted = 0;
                    foreach (var window in EphemerisImportSourcePolicy.GetWindowsForTarget(slug, effectiveStart, effectiveEnd, sampleRateOverride)) {
                        int windowInserted = await ImportBodyChunksAsync(bodyId, slug, jplId, window.StartJd, window.EndJd, window.Step, ct, _debug);
                        if (windowInserted > 0 && inserted == 0) {
                            await using var mCtx = await dbContextFactory.CreateDbContextAsync(ct);
                            await MarkHasEphemerisAsync(mCtx, bodyId, ct);
                        }
                        inserted += windowInserted;
                    }

                    await using var cCtx = await dbContextFactory.CreateDbContextAsync(ct);
                    if (await IsRangeFullyLoggedAsync(cCtx, bodyId, minJd, maxJd, step, ct))
                        await SetCompletedEphemerisAsync(cCtx, bodyId, ct);

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

        bool isTargetedRun = slugFilter is { Count: > 0 } || bodyIdFilter is { Count: > 0 };

        if (!isTargetedRun) {
            var resetCount = await ResetBodiesWithZeroChunksAsync(hMax, cancellationToken);
            if (resetCount > 0)
                Console.WriteLine($"Reset {resetCount} bodies with zero-sample chunks (will retry next run).");

            var remaining = await CountIncompleteBodiesAsync(hMax, cancellationToken);
            Console.WriteLine(remaining > 0
                ? $"{remaining} bodies still incomplete — run again to continue."
                : "All target bodies complete.");
        }

        return new EphemerisSampleImportResult(totalBodies, totalSamples, 0);
    }

    // ── Core chunk-import loop ────────────────────────────────────────────────

    private const int    BoundaryMaxShrinkDays   = 10;
    private const double ChunkMatchToleranceJd   = 2.0;

    private async Task<int> ImportBodyChunksAsync(
        int bodyId, string slug, string horizonsCommand,
        double startJd, double endJd, TimeSpan step,
        CancellationToken ct,
        bool debug)
    {
        IReadOnlyCollection<(double Start, double End)> loggedChunks;
        await using (var loadCtx = await dbContextFactory.CreateDbContextAsync(ct))
            loggedChunks = await LoadLoggedChunksAsync(loadCtx, bodyId, startJd, endJd, ct);

        var allChunks   = ChunkRange(startJd, endJd, step).ToList();
        int totalChunks = allChunks.Count;
        int totalInserted = 0;

        await Parallel.ForEachAsync(
            allChunks.Select((c, i) => (c.Start, c.End, Index: i + 1)),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (item, innerCt) =>
            {
                if (IsChunkLogged(loggedChunks, item.Start, item.End)) return;

                Console.WriteLine($"    {slug} {item.Index}/{totalChunks} JD{item.Start:F0}..JD{item.End:F0}");

                await using var ctx = await dbContextFactory.CreateDbContextAsync(innerCt);
                await ctx.Database.OpenConnectionAsync(innerCt);

                var fetchStart = DateTime.UtcNow;
                var fetch = await FetchAndInsertChunkAsync(ctx, bodyId, slug, horizonsCommand, item.Start, item.End, step, innerCt, debug);
                var fetchEnd = DateTime.UtcNow;
                if (debug)
                {
                    var fetchMs = (fetchEnd - fetchStart).TotalMilliseconds;
                    Console.WriteLine($"      [DEBUG] Fetch+Insert for chunk took {fetchMs:N0} ms");
                }
                if (fetch.Inserted < 0) return;

                var logStart = fetch.EffStart;
                var logEnd   = fetch.EffEnd;
                var inserted = fetch.Inserted;

                if (inserted == 0) {
                    var isFirstChunk = Math.Abs(item.Start - startJd) < 0.5;
                    var isLastChunk  = Math.Abs(item.End   - endJd)   < 0.5;

                    if (isFirstChunk || isLastChunk) {
                        int shrink = 1;
                        while (shrink <= BoundaryMaxShrinkDays) {
                            var retryStart = isFirstChunk ? item.Start + shrink : item.Start;
                            var retryEnd   = isLastChunk  ? item.End   - shrink : item.End;
                            if (retryStart >= retryEnd) break;

                            await Task.Delay(150, innerCt);
                            var rf = await FetchAndInsertChunkAsync(ctx, bodyId, slug, horizonsCommand, retryStart, retryEnd, step, innerCt, debug);

                            if (rf.Inserted < 0) { await Task.Delay(500, innerCt); continue; }
                            shrink++;

                            if (rf.Inserted == 0) continue;

                            inserted = rf.Inserted;
                            logStart = isFirstChunk ? retryStart : item.Start;
                            logEnd   = isLastChunk  ? retryEnd   : item.End;
                            await UpdateBodyEphemerisBoundaryAsync(ctx, bodyId,
                                isFirstChunk ? retryStart : null,
                                isLastChunk  ? retryEnd   : null, innerCt);
                            break;
                        }
                        if (inserted < 0) inserted = 0;
                    }
                }

                if (logStart != item.Start || logEnd != item.End)
                    await DeleteLogChunkAsync(ctx, bodyId, item.Start, item.End, innerCt);
                await LogChunkAsync(ctx, bodyId, logStart, logEnd, inserted, innerCt);
                Interlocked.Add(ref totalInserted, inserted);

                if (item.End < endJd)
                    await Task.Delay(150, innerCt);
            });

        return totalInserted;
    }

    private static bool IsChunkLogged(
        IReadOnlyCollection<(double Start, double End)> loggedChunks,
        double startJd, double endJd)
    {
        foreach (var (s, e) in loggedChunks)
            if (Math.Abs(s - startJd) <= ChunkMatchToleranceJd && Math.Abs(e - endJd) <= ChunkMatchToleranceJd)
                return true;
        return false;
    }

    private async Task<ChunkFetchResult> FetchAndInsertChunkAsync(
        SolWriteDbContext ctx,
        int bodyId, string slug, string horizonsCommand,
        double winStart, double winEnd, TimeSpan step,
        CancellationToken ct,
        bool debug)
    {
        var requestUri = BuildHorizonsVectorsUri(horizonsCommand, winStart, winEnd, step);
        var httpStart = DateTime.UtcNow;
        using var response = await httpClient.GetAsync(requestUri, ct);
        var httpEnd = DateTime.UtcNow;
        if (debug)
        {
            var httpMs = (httpEnd - httpStart).TotalMilliseconds;
            Console.WriteLine($"      [DEBUG] HTTP fetch took {httpMs:N0} ms");
        }

        if (!response.IsSuccessStatusCode)
            return new(-1, winStart, winEnd);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("error", out var errEl)) {
            var errText  = errEl.GetString() ?? "";
            var adjStart = winStart;
            var adjEnd   = winEnd;

            // Debug output for rejected chunk
            int expectedLines = (int)Math.Round((winEnd - winStart) / step.TotalDays) + 1;
            Console.WriteLine($"  [DEBUG] Horizons API error for {slug}: JD{winStart:F0}..JD{winEnd:F0} (step={step}, expected lines={expectedLines}, MaxLinesPerRequest={MaxLinesPerRequest})");
            Console.WriteLine($"  [DEBUG] Error message: {errText}");

            var priorM = System.Text.RegularExpressions.Regex.Match(errText,
                @"prior to A\.D\.\s+(\d+)-([A-Z]{3})-(\d+)\s+([\d:.]+)");
            if (priorM.Success) {
                adjStart = ParseHorizonsErrorDateToJd(priorM);
                await UpdateBodyEphemerisBoundaryAsync(ctx, bodyId, adjStart, null, ct);
                await DeleteOutOfRangeLogChunksAsync(ctx, bodyId, adjStart, null, ct);
            }

            var afterM = System.Text.RegularExpressions.Regex.Match(errText,
                @"after A\.D\.\s+(\d+)-([A-Z]{3})-(\d+)\s+([\d:.]+)");
            if (afterM.Success) {
                adjEnd = ParseHorizonsErrorDateToJd(afterM);
                await UpdateBodyEphemerisBoundaryAsync(ctx, bodyId, null, adjEnd, ct);
                await DeleteOutOfRangeLogChunksAsync(ctx, bodyId, null, adjEnd, ct);
            }

            if (adjStart < adjEnd && (adjStart > winStart || adjEnd < winEnd)) {
                Console.WriteLine($"    {slug} boundary adjusted JD{adjStart:F0}..JD{adjEnd:F0}");
                return await FetchAndInsertChunkAsync(ctx, bodyId, slug, horizonsCommand, adjStart, adjEnd, step, ct, debug);
            }
            return new(0, winStart, winEnd);
        }

        if (!doc.RootElement.TryGetProperty("result", out var resultEl)) return new(0, winStart, winEnd);
        var resultText = resultEl.GetString();
        if (string.IsNullOrEmpty(resultText)) return new(0, winStart, winEnd);

        if (resultText.Contains("To SELECT, enter record #")) {
            var record = PickBestApparitionRecord(resultText, (winStart + winEnd) / 2.0);
            if (record == null) return new(0, winStart, winEnd);
            Console.WriteLine($"    {slug} → apparition record {record}");
            return await FetchAndInsertChunkAsync(ctx, bodyId, slug, $"{record};", winStart, winEnd, step, ct, debug);
        }

        if (resultText.Contains("$$SOE")) {
            var samples = ParseHorizonsVectorCsv(bodyId, resultText, slug);
            if (samples.Count > 0) {
                var insertStart = DateTime.UtcNow;
                await InsertSamplesAsync(ctx, samples, ct);
                var insertEnd = DateTime.UtcNow;
                if (debug)
                {
                    var insertMs = (insertEnd - insertStart).TotalMilliseconds;
                    Console.WriteLine($"      [DEBUG] DB insert/merge took {insertMs:N0} ms");
                }
                return new(samples.Count, winStart, winEnd);
            }
        }

        return new(0, winStart, winEnd);
    }

    private readonly record struct ChunkFetchResult(int Inserted, double EffStart, double EffEnd);

    // ── Retry zero-sample chunks ──────────────────────────────────────────────

    public async Task<int> RetryZeroSamplesAsync(double maxShrinkDays, CancellationToken ct)
    {
        var zeros = await LoadZeroSampleChunksAsync(ct);
        Console.WriteLine($"Retrying {zeros.Count} zero-sample chunks (shrink up to {maxShrinkDays} day(s) on boundary edges).");
        if (zeros.Count == 0) return 0;

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
                using var response = await httpClient.GetAsync(requestUri, ct);
                if (!response.IsSuccessStatusCode) { await Task.Delay(500, ct); continue; }
                shrink++;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (doc.RootElement.TryGetProperty("result", out var resultEl)) {
                    var resultText = resultEl.GetString();
                    if (!string.IsNullOrEmpty(resultText) && resultText.Contains("$$SOE")) {
                        var samples = ParseHorizonsVectorCsv(bodyId, resultText, slug);
                        if (samples.Count > 0) {
                            await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
                            await ctx.Database.OpenConnectionAsync(ct);
                            inserted = await InsertSamplesAsync(ctx, samples, ct);
                            if (inserted > 0) {
                                logStart = isFirstChunk ? retryStart : startJd;
                                logEnd   = isLastChunk  ? retryEnd   : endJd;
                                await DeleteLogChunkAsync(ctx, bodyId, startJd, endJd, ct);
                                await LogChunkAsync(ctx, bodyId, logStart, logEnd, inserted, ct);
                                await UpdateBodyEphemerisBoundaryAsync(ctx, bodyId,
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

    // ── Horizons API helpers ──────────────────────────────────────────────────

    private static IEnumerable<(double Start, double End)> ChunkRange(double startJd, double endJd, TimeSpan step)
    {
        int maxLinesPerRequest = MaxLinesPerRequest;
        double windowDays  = maxLinesPerRequest * step.TotalDays;
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
        string stepStr = step.TotalMinutes < 60
            ? $"{Math.Max(1, (int)Math.Round(step.TotalMinutes, MidpointRounding.AwayFromZero))} m"
            : $"{Math.Max(1, (int)Math.Round(step.TotalHours,   MidpointRounding.AwayFromZero))} h";
        var quotedStep = Uri.EscapeDataString($"'{stepStr}'");

        return $"{HorizonsApiBase}?format=json&COMMAND={quotedCommand}&OBJ_DATA='NO'&MAKE_EPHEM='YES'" +
               $"&EPHEM_TYPE='VECTORS'&CENTER='500@0'&REF_PLANE='ECLIPTIC'&REF_SYSTEM='ICRF'" +
               $"&OUT_UNITS='AU-D'&TIME_TYPE='UT'&START_TIME={quotedStart}&STOP_TIME={quotedEnd}" +
               $"&STEP_SIZE={quotedStep}&VEC_TABLE='2'&CSV_FORMAT='YES'";
    }

    private static IReadOnlyList<SampleImportRow> ParseHorizonsVectorCsv(int bodyId, string resultText, string slug)
    {
        var rows  = new List<SampleImportRow>();
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

    private static string? PickBestApparitionRecord(string resultText, double midJd)
    {
        var midYear = 2000.0 + (midJd - 2451545.0) / 365.25;
        string? best = null;
        double bestDiff = double.MaxValue;

        foreach (var line in resultText.Split('\n')) {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\d{5,})\s+(\d{4})\s+");
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out var year)) continue;
            var diff = Math.Abs(year - midYear);
            if (diff < bestDiff) { bestDiff = diff; best = m.Groups[1].Value; }
        }

        return best;
    }

    // ── Database helpers ──────────────────────────────────────────────────────

    private async Task<List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)>>
        LoadBodiesByIdAsync(IReadOnlyList<int> bodyIds, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await ctx.Bodies
            .Where(b => b.IsActive && b.JplHorizonsId != null && b.EphemerisMinJD != null && b.EphemerisMaxJD != null
                     && bodyIds.Contains(b.BodyId))
            .OrderBy(b => b.BodyId)
            .Select(b => new { b.BodyId, b.Slug, b.JplHorizonsId, b.EphemerisMinJD, b.EphemerisMaxJD })
            .ToListAsync(ct);
        return rows.ConvertAll(r => (r.BodyId, r.Slug, r.JplHorizonsId!, r.EphemerisMinJD!.Value, r.EphemerisMaxJD!.Value));
    }

    private async Task<List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)>>
        LoadBodiesBySlugAsync(IReadOnlyList<string> slugs, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await ctx.Bodies
            .Where(b => b.IsActive && b.JplHorizonsId != null && b.EphemerisMinJD != null && b.EphemerisMaxJD != null
                     && slugs.Contains(b.Slug))
            .OrderBy(b => b.Slug)
            .Select(b => new { b.BodyId, b.Slug, b.JplHorizonsId, b.EphemerisMinJD, b.EphemerisMaxJD })
            .ToListAsync(ct);
        return rows.ConvertAll(r => (r.BodyId, r.Slug, r.JplHorizonsId!, r.EphemerisMinJD!.Value, r.EphemerisMaxJD!.Value));
    }

    private async Task<List<(int BodyId, string Slug, string JplId, double MinJd, double MaxJd)>>
        LoadBodiesForEphemerisAsync(double? hMax, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var oldTimeout = ctx.Database.GetCommandTimeout();
        ctx.Database.SetCommandTimeout(300); // 5 minutes
        try
        {
            var query = ctx.Bodies.Where(b =>
                b.IsActive && !b.CompletedEphemeris &&
                b.JplHorizonsId != null && b.EphemerisMinJD != null && b.EphemerisMaxJD != null);

            if (hMax.HasValue)
                query = query.Where(b => b.H_AbsMag == null || b.H_AbsMag <= hMax.Value);
            else
                query = query.Where(b => b.H_AbsMag == null && b.Source != "mpcorb");

            var rows = await query
                .OrderBy(b => b.H_AbsMag).ThenBy(b => b.Slug)
                .Select(b => new { b.BodyId, b.Slug, b.JplHorizonsId, b.EphemerisMinJD, b.EphemerisMaxJD })
                .ToListAsync(ct);

            return rows.ConvertAll(r => (r.BodyId, r.Slug, r.JplHorizonsId!, r.EphemerisMinJD!.Value, r.EphemerisMaxJD!.Value));
        }
        finally
        {
            ctx.Database.SetCommandTimeout(oldTimeout);
        }
    }

    private static async Task<List<(double Start, double End)>> LoadLoggedChunksAsync(
        SolWriteDbContext ctx, int bodyId, double startJd, double endJd, CancellationToken ct)
    {
        var rows = await ctx.EphemerisImportLog
            .Where(l => l.BodyId == bodyId && l.StartJd >= startJd && l.EndJd <= endJd && l.SampleCount > 0)
            .Select(l => new { l.StartJd, l.EndJd })
            .ToListAsync(ct);
        return rows.ConvertAll(r => (r.StartJd, r.EndJd));
    }

    private static Task DeleteOutOfRangeLogChunksAsync(
        SolWriteDbContext ctx, int bodyId, double? newMinJd, double? newMaxJd, CancellationToken ct)
    {
        var query = ctx.EphemerisImportLog.Where(l => l.BodyId == bodyId);
        if (newMinJd.HasValue && newMaxJd.HasValue)
            query = query.Where(l => l.StartJd < newMinJd.Value || l.EndJd > newMaxJd.Value);
        else if (newMinJd.HasValue)
            query = query.Where(l => l.StartJd < newMinJd.Value);
        else if (newMaxJd.HasValue)
            query = query.Where(l => l.EndJd > newMaxJd.Value);
        else
            return Task.CompletedTask;

        return query.ExecuteDeleteAsync(ct);
    }

    private static Task DeleteLogChunksInRangeAsync(
        SolWriteDbContext ctx, int bodyId, double startJd, double endJd, CancellationToken ct) =>
        ctx.EphemerisImportLog
           .Where(l => l.BodyId == bodyId && l.StartJd >= startJd && l.EndJd <= endJd)
           .ExecuteDeleteAsync(ct);

    private static Task DeleteLogChunkAsync(
        SolWriteDbContext ctx, int bodyId, double startJd, double endJd, CancellationToken ct) =>
        ctx.EphemerisImportLog
           .Where(l => l.BodyId == bodyId && l.StartJd == startJd && l.EndJd == endJd)
           .ExecuteDeleteAsync(ct);

    private static async Task LogChunkAsync(
        SolWriteDbContext ctx, int bodyId, double startJd, double endJd, int sampleCount, CancellationToken ct)
    {
        // MERGE upsert: update SampleCount if the row already exists, insert if new.
        const string sql = @"
MERGE dbo.EphemerisImportLog AS tgt
USING (SELECT @bodyId AS BodyId, @startJd AS StartJd, @endJd AS EndJd) AS src
  ON tgt.BodyId = src.BodyId AND tgt.StartJd = src.StartJd AND tgt.EndJd = src.EndJd
WHEN MATCHED THEN
  UPDATE SET SampleCount = @sampleCount, ImportedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT (BodyId, StartJd, EndJd, SampleCount)
  VALUES (src.BodyId, src.StartJd, src.EndJd, @sampleCount);";

        await ctx.Database.ExecuteSqlRawAsync(sql,
            [new SqlParameter("@bodyId",      bodyId),
             new SqlParameter("@startJd",     startJd),
             new SqlParameter("@endJd",       endJd),
             new SqlParameter("@sampleCount", sampleCount)], ct);
    }

    private static async Task<bool> IsRangeFullyLoggedAsync(
        SolWriteDbContext ctx, int bodyId, double minJd, double maxJd, TimeSpan step, CancellationToken ct)
    {
        var expectedChunks = ChunkRange(minJd, maxJd, step).ToList();
        if (expectedChunks.Count == 0) return true;

        var logged = await ctx.EphemerisImportLog
            .Where(l => l.BodyId == bodyId && l.StartJd >= minJd && l.EndJd <= maxJd && l.SampleCount > 0)
            .Select(l => new { l.StartJd, l.EndJd })
            .ToListAsync(ct);

        const double tol = 2.0;
        foreach (var (s, e) in expectedChunks)
            if (!logged.Any(l => Math.Abs(l.StartJd - s) <= tol && Math.Abs(l.EndJd - e) <= tol))
                return false;

        return true;
    }

    private static Task SetCompletedEphemerisAsync(SolWriteDbContext ctx, int bodyId, CancellationToken ct) =>
        ctx.Bodies
           .Where(b => b.IsActive && b.BodyId == bodyId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.CompletedEphemeris, true)
               .SetProperty(b => b.UpdatedUtc, DateTime.UtcNow), ct);

    private static Task MarkHasEphemerisAsync(SolWriteDbContext ctx, int bodyId, CancellationToken ct) =>
        ctx.Bodies
           .Where(b => b.BodyId == bodyId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(b => b.HasEphemeris, true)
               .SetProperty(b => b.UpdatedUtc, DateTime.UtcNow), ct);

    private static async Task UpdateBodyEphemerisBoundaryAsync(
        SolWriteDbContext ctx, int bodyId, double? newMinJd, double? newMaxJd, CancellationToken ct)
    {
        if (newMinJd == null && newMaxJd == null) return;

        var parts     = new List<string> { "UpdatedUtc = SYSUTCDATETIME()" };
        var sqlParams = new List<object> { new SqlParameter("@bodyId", bodyId) };

        if (newMinJd != null) {
            parts.Add("EphemerisMinJD = @minJd");
            parts.Add("EphemerisMinStr = @minStr");
            sqlParams.Add(new SqlParameter("@minJd",  newMinJd.Value));
            sqlParams.Add(new SqlParameter("@minStr", JdToDisplayStr(newMinJd.Value)));
        }
        if (newMaxJd != null) {
            parts.Add("EphemerisMaxJD = @maxJd");
            parts.Add("EphemerisMaxStr = @maxStr");
            sqlParams.Add(new SqlParameter("@maxJd",  newMaxJd.Value));
            sqlParams.Add(new SqlParameter("@maxStr", JdToDisplayStr(newMaxJd.Value)));
        }

        // Column names in `parts` are hardcoded strings — not user input, no injection risk.
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync(
            $"UPDATE dbo.Bodies SET {string.Join(", ", parts)} WHERE BodyId = @bodyId;",
            sqlParams, ct);
#pragma warning restore EF1002
    }

    private static string JdToDisplayStr(double jd)
    {
        var s = JulianDateConverter.ToHorizonsDateString(jd);
        return s.StartsWith("BC ", StringComparison.Ordinal) ? "B.C. " + s[3..] : "A.D. " + s;
    }

    private async Task<int> CountIncompleteBodiesAsync(double? hMax, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var query = ctx.Bodies.Where(b =>
            b.IsActive && !b.CompletedEphemeris &&
            b.JplHorizonsId != null && b.EphemerisMinJD != null && b.EphemerisMaxJD != null);

        if (hMax.HasValue)
            query = query.Where(b => b.H_AbsMag == null || b.H_AbsMag <= hMax.Value);
        else
            query = query.Where(b => b.H_AbsMag == null && b.Source != "mpcorb");

        return await query.CountAsync(ct);
    }

    private async Task<int> ResetBodiesWithZeroChunksAsync(double? hMax, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var query = ctx.Bodies.Where(b =>
            b.IsActive && b.CompletedEphemeris && b.JplHorizonsId != null &&
            ctx.EphemerisImportLog.Any(l => l.BodyId == b.BodyId && l.SampleCount == 0));

        if (hMax.HasValue)
            query = query.Where(b => b.H_AbsMag == null || b.H_AbsMag <= hMax.Value);
        else
            query = query.Where(b => b.H_AbsMag == null && b.Source != "mpcorb");

        return await query.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.CompletedEphemeris, false)
            .SetProperty(b => b.UpdatedUtc, DateTime.UtcNow), ct);
    }

    private async Task ResetCompletedEphemerisAsync(IReadOnlyList<int> bodyIds, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        await ctx.Bodies
            .Where(b => b.IsActive && bodyIds.Contains(b.BodyId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.CompletedEphemeris, false)
                .SetProperty(b => b.UpdatedUtc, DateTime.UtcNow), ct);
    }

    private async Task<List<(int BodyId, string Slug, string JplId, double StartJd, double EndJd, double EphMinJd, double EphMaxJd)>>
        LoadZeroSampleChunksAsync(CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await ctx.EphemerisImportLog
            .Join(ctx.Bodies, l => l.BodyId, b => b.BodyId, (l, b) => new { l, b })
            .Where(x => x.l.SampleCount == 0 && x.b.JplHorizonsId != null
                     && x.b.EphemerisMinJD != null && x.b.EphemerisMaxJD != null)
            .OrderBy(x => x.l.BodyId).ThenBy(x => x.l.StartJd)
            .Select(x => new {
                x.l.BodyId, x.b.Slug, x.b.JplHorizonsId,
                x.l.StartJd, x.l.EndJd, x.b.EphemerisMinJD, x.b.EphemerisMaxJD
            })
            .ToListAsync(ct);

        return rows.ConvertAll(r => (
            r.BodyId, r.Slug, r.JplHorizonsId!,
            r.StartJd, r.EndJd, r.EphemerisMinJD!.Value, r.EphemerisMaxJD!.Value));
    }

    // ── Sample bulk insert ────────────────────────────────────────────────────

    // Loads samples into a connection-scoped staging table via SqlBulkCopy,
    // then MERGE-inserts rows not already in dbo.EphemerisSamples.
    // Requires ctx.Database.OpenConnectionAsync() to have been called so the
    // temp table is pinned to the same physical connection as the MERGE.
    private static async Task<int> InsertSamplesAsync(
        SolWriteDbContext ctx, IReadOnlyList<SampleImportRow> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return 0;

        var conn = (SqlConnection)ctx.Database.GetDbConnection();

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
        await ctx.Database.ExecuteSqlRawAsync(createStaging, ct);

        using var sourceReader = new SampleImportRowDataReader(samples);
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null) {
            DestinationTableName = "#EphemerisStaging",
            BulkCopyTimeout      = 0,
            EnableStreaming       = true,
            BatchSize            = 10_000
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

        const string mergeSql = @"
MERGE dbo.EphemerisSamples AS tgt
USING #EphemerisStaging AS src ON tgt.BodyId = src.BodyId AND tgt.SampleJd = src.SampleJd
WHEN NOT MATCHED BY TARGET THEN INSERT (
    BodyId, SampleJd, X_AU, Y_AU, Z_AU, VX_AUPerDay, VY_AUPerDay, VZ_AUPerDay, Frame, Source
) VALUES (
    src.BodyId, src.SampleJd, src.X_AU, src.Y_AU, src.Z_AU,
    src.VX_AUPerDay, src.VY_AUPerDay, src.VZ_AUPerDay, src.Frame, src.Source
);";

        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        try {
            ctx.Database.SetCommandTimeout(600);
            int inserted = await ctx.Database.ExecuteSqlRawAsync(mergeSql, ct);
            ctx.Database.SetCommandTimeout(null);
            await tx.CommitAsync(ct);
            return inserted;
        }
        catch (SqlException ex) when (ex.Number == 2627) {
            await tx.RollbackAsync(ct);
            Console.WriteLine($"    [dup] chunk already fully present, skipping insert.");
            return 0;
        }
    }

    // ── DbDataReader for SqlBulkCopy ──────────────────────────────────────────

    private sealed class SampleImportRowDataReader(IReadOnlyList<SampleImportRow> rows) : DbDataReader
    {
        private static readonly string[] _names = ["BodyId", "SampleJd", "X_AU", "Y_AU", "Z_AU", "VX_AUPerDay", "VY_AUPerDay", "VZ_AUPerDay", "Frame", "Source"];
        private int _index = -1;

        public override int  FieldCount     => _names.Length;
        public override bool HasRows        => rows.Count > 0;
        public override bool IsClosed       => false;
        public override int  RecordsAffected => -1;
        public override int  Depth          => 0;

        public override bool Read()    => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override string GetName(int ordinal)    => _names[ordinal];
        public override int    GetOrdinal(string name) => Array.IndexOf(_names, name);
        public override Type   GetFieldType(int ordinal) => ordinal switch { 0 => typeof(int), 8 or 9 => typeof(string), _ => typeof(double) };

        public override object GetValue(int ordinal)
        {
            var r = rows[_index];
            return ordinal switch
            {
                0 => r.BodyId, 1 => r.SampleJd,
                2 => r.X,      3 => r.Y,      4 => r.Z,
                5 => r.Vx,     6 => r.Vy,     7 => r.Vz,
                8 => EphemerisFrame, 9 => EphemerisSource,
                _ => throw new IndexOutOfRangeException()
            };
        }

        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }

        public override bool   IsDBNull(int ordinal)   => false;
        public override int    GetInt32(int ordinal)   => (int)GetValue(ordinal);
        public override double GetDouble(int ordinal)  => (double)GetValue(ordinal);
        public override string GetString(int ordinal)  => (string)GetValue(ordinal);
        public override object this[int ordinal]       => GetValue(ordinal);
        public override object this[string name]       => GetValue(GetOrdinal(name));

        public override bool    GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte    GetByte(int ordinal)    => throw new NotSupportedException();
        public override long    GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char    GetChar(int ordinal)    => throw new NotSupportedException();
        public override long    GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override float   GetFloat(int ordinal)   => throw new NotSupportedException();
        public override Guid    GetGuid(int ordinal)    => throw new NotSupportedException();
        public override short   GetInt16(int ordinal)   => throw new NotSupportedException();
        public override long    GetInt64(int ordinal)   => throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override string  GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override Stream  GetStream(int ordinal)  => throw new NotSupportedException();
        public override TextReader GetTextReader(int ordinal) => throw new NotSupportedException();
    }

    private readonly record struct SampleImportRow(
        int BodyId, double SampleJd,
        double X, double Y, double Z,
        double Vx, double Vy, double Vz);
}
