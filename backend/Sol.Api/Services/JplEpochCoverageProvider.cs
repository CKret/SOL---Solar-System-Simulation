using System.Text.Json;
using System.Collections.Concurrent;
namespace Sol.Api.Services;

/// <summary>
/// Fetches and caches epoch coverage (MinJD, MaxJD) for JPL Horizons objects from the support API.
/// jd_min / jd_max are used directly — no date-string parsing required.
/// </summary>
public sealed class JplEpochCoverageProvider
{
    private static readonly string[] ApiUrls =
    [
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=planets&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=js&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=ss&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=us&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=ns&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=os&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=spacecraft&time-span=1",
        "https://ssd.jpl.nasa.gov/api/horizons_support.api?list=special&time-span=1",
    ];

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, (double MinJD, double MaxJD, string MinStr, string MaxStr)> _epochMap = new();
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public JplEpochCoverageProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            foreach (var url in ApiUrls)
            {
                try
                {
                    using var resp = await _httpClient.GetAsync(url, cancellationToken);
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    ParseResponse(doc.RootElement);
                }
                catch { }
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Fetches epoch range for a single body by SPK ID (for small bodies not covered by the list endpoints).
    /// Caches the result for subsequent calls.
    /// </summary>
    public async Task<(double MinJD, double MaxJD, string MinStr, string MaxStr)?> FetchAndCacheBodyAsync(string spkid, CancellationToken cancellationToken)
    {
        if (_epochMap.TryGetValue(spkid, out var cached)) return cached;
        try
        {
            var url = $"https://ssd.jpl.nasa.gov/api/horizons_support.api?spk={spkid}&time-span=1";
            using var resp = await _httpClient.GetAsync(url, cancellationToken);
            // JPL returns HTTP 300 for periodic comets with multiple orbit solutions.
            // The JSON body is still valid — do not call EnsureSuccessStatusCode().
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            ParseResponse(doc.RootElement);
        }
        catch { }
        return _epochMap.TryGetValue(spkid, out var result) ? result : null;
    }

    // Periodic comets have multiple historical orbit solutions. The support API returns
    // code 300 with a flat list of solution stubs — none have jd_min/jd_max.
    // Use Jupiter's range (1600-01-10 to 2200-01-10) as a conservative comet default.
    private const double CometMinJD = 2305456.5;
    private const double CometMaxJD = 2524602.5;
    private const string CometMinStr = "1600-01-10 00:00";
    private const string CometMaxStr = "2200-01-10 00:00";

    private void ParseResponse(JsonElement root)
    {
        // Format C: { "code": "300", "list_type": "orbits", "list": [ ...orbit stubs without jd range... ] }
        // This occurs for periodic comets (multiple apparitions). Stubs lack jd_min/jd_max.
        if (root.TryGetProperty("code", out var codeProp) && codeProp.GetString() == "300" &&
            root.TryGetProperty("list", out var orbitList) && orbitList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in orbitList.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is { Length: > 0 } id)
                {
                    _epochMap.TryAdd(id, (CometMinJD, CometMaxJD, CometMinStr, CometMaxStr));
                    break;
                }
            }
            return;
        }

        // Format A: { "list": [ { "name": "planets", "list": [ ...body objects... ] }, ... ] }
        if (root.TryGetProperty("list", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in listEl.EnumerateArray())
            {
                if (group.TryGetProperty("list", out var subList) && subList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var obj in subList.EnumerateArray())
                        AddEpochFromElement(obj);
                }
                else
                {
                    AddEpochFromElement(group);
                }
            }
        }
        // Format B: { "data": { ...single body object... } }
        else if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
        {
            AddEpochFromElement(dataEl);
        }
    }

    private void AddEpochFromElement(JsonElement obj)
    {
        if (!obj.TryGetProperty("id", out var idProp)) return;
        if (!obj.TryGetProperty("jd_min", out var jdMinProp) || jdMinProp.ValueKind != JsonValueKind.Number) return;
        if (!obj.TryGetProperty("jd_max", out var jdMaxProp) || jdMaxProp.ValueKind != JsonValueKind.Number) return;

        var id = idProp.GetString();
        if (string.IsNullOrWhiteSpace(id)) return;

        var minStr = obj.TryGetProperty("cd_min", out var cdMin) ? cdMin.GetString() ?? "" : "";
        var maxStr = obj.TryGetProperty("cd_max", out var cdMax) ? cdMax.GetString() ?? "" : "";
        _epochMap[id] = (jdMinProp.GetDouble(), jdMaxProp.GetDouble(), minStr, maxStr);
    }

    public (double MinJD, double MaxJD, string MinStr, string MaxStr)? TryGetEpochRange(string jplId)
    {
        if (_epochMap.TryGetValue(jplId, out var range)) return range;
        return null;
    }
}
