using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sol.Api.Services
{
    public static class QAdProvider
    {
        private static readonly string PlanetDataPath = Path.Combine(AppContext.BaseDirectory, "Resources", "planet_aphelion_perihelion.json");
        private static Dictionary<string, (double, double)>? _planetData;
        private static readonly HttpClient _http = new();

        public static async Task<(double? qResult, double? adResult)> GetQAdAsync(string name, string? sbdbDesignation = null, string? horizonsCommand = null, string? category = null)
        {


            // 1. Major planets & Pluto: static table
            var planetTupleResult = await TryGetPlanetDataAsync(name);
            if (planetTupleResult is { } planetDataResult)
                return (planetDataResult.Item1, planetDataResult.Item2);

            // 2. Small bodies: SBDB API
            if (!string.IsNullOrWhiteSpace(sbdbDesignation))
            {
                var url = $"https://ssd-api.jpl.nasa.gov/sbdb.api?sstr={Uri.EscapeDataString(sbdbDesignation)}";
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("orbit", out var orbitElemResult))
                {
                    double? qVal = orbitElemResult.TryGetProperty("q", out var qObj) && qObj.TryGetDouble(out var qNum) ? qNum : null;
                    double? adVal = orbitElemResult.TryGetProperty("ad", out var adObj) && adObj.TryGetDouble(out var adNum) ? adNum : null;
                    if (qVal.HasValue && adVal.HasValue)
                        return (qVal, adVal);
                }
            }

            // 3. Moons: barycentric from Horizons
            if (category != null && category.ToLower().Contains("moon") && !string.IsNullOrWhiteSpace(horizonsCommand))
            {
                // Use Horizons API, barycentric elements
                var url = $"https://ssd.jpl.nasa.gov/api/horizons.api?format=json&COMMAND={Uri.EscapeDataString(horizonsCommand)}&MAKE_EPHEM=NO&TABLE_TYPE=ELEMENTS&CENTER='500@0'&OBJ_DATA=YES";
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var resultElemMoon))
                {
                    var resultStrMoon = resultElemMoon.GetString();
                    if (resultStrMoon != null)
                    {
                        var moonQ = TryParseElement(resultStrMoon, "Perihelion dist");
                        var moonAd = TryParseElement(resultStrMoon, "Aphelion dist");
                        if (moonQ.HasValue && moonAd.HasValue)
                            return (moonQ, moonAd);
                    }
                }
            }

            // 4. Spacecraft or unknown: null
            return (null, null);
        }

        private static async Task<(double, double)?> TryGetPlanetDataAsync(string name)
        {
            if (_planetData == null)
            {
                var json = await File.ReadAllTextAsync(PlanetDataPath);
                var arr = JsonDocument.Parse(json).RootElement.EnumerateArray();
                _planetData = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in arr)
                {
                    var planetName = el.GetProperty("name").GetString()!;
                    var q = el.GetProperty("perihelion_au").GetDouble();
                    var ad = el.GetProperty("aphelion_au").GetDouble();
                    _planetData[planetName] = (q, ad);
                }
            }
            if (_planetData.TryGetValue(name, out var planetTuple))
                return planetTuple;
            return null;
        }

        private static double? TryParseElement(string result, string label)
        {
            var idxElem = result.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idxElem < 0) return null;
            var lineElem = result.Substring(idxElem, Math.Min(80, result.Length - idxElem));
            var matchElem = System.Text.RegularExpressions.Regex.Match(lineElem, @"([-+]?[0-9]*\.?[0-9]+)");
            if (matchElem.Success && double.TryParse(matchElem.Groups[1].Value, out var valElem))
                return valElem;
            return null;
        }
    }
}
