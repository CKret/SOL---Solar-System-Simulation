using Sol.Api.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sol.Api.Services;

public sealed partial class JsSimulationCatalogReader(IWebHostEnvironment environment) : ISimulationCatalogReader
{
  private readonly string _repoRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));

  public IReadOnlyList<CatalogBodySeed> ReadBodies()
  {
    var solarSystemPath = Path.Combine(_repoRoot, "js", "solar_system.js");
    var voyagerPath = Path.Combine(_repoRoot, "js", "voyager_trajectories.js");

    if (!File.Exists(solarSystemPath)) throw new FileNotFoundException("Could not locate solar_system.js for import.", solarSystemPath);
    if (!File.Exists(voyagerPath)) throw new FileNotFoundException("Could not locate voyager_trajectories.js for import.", voyagerPath);

    var solarText = File.ReadAllText(solarSystemPath);
    var voyagerText = File.ReadAllText(voyagerPath);

    var seeds = new List<CatalogBodySeed>
    {
      CreateSeed("sun", "Sun", "Star", "star", null, 0, new { source = "manual", catalog = "system" })
    };

    seeds.AddRange(ParsePlanets(solarText));
    seeds.AddRange(ParseMoons(solarText));
    seeds.AddRange(ParseDwarfs(solarText));
    seeds.AddRange(ParseComets(solarText));
    seeds.AddRange(ParseVoyagers(voyagerText));

    return seeds;
  }

  private static IEnumerable<CatalogBodySeed> ParsePlanets(string text)
  {
    return ParseInlineObjects(GetArrayBody(text, "const PD = ["), (match, index) =>
    {
      var body = match.Groups["body"].Value;
      var sourceName = GetFieldValue(body, "name") ?? throw new InvalidOperationException("Planet entry is missing a name.");
      var displayName = NormalizeDisplayName(sourceName);
      var type = GetFieldValue(body, "type") ?? "Planet";
      return CreateSeed(Slugify(displayName), displayName, type, "planet", "sun", 100 + index, new
      {
        source = "solar_system.js",
        catalog = "PD",
        sourceName,
        kind = "planet"
      });
    });
  }

  private static IEnumerable<CatalogBodySeed> ParseMoons(string text)
  {
    return ParseInlineObjects(GetArrayBody(text, "const MOON_DATA = ["), (match, index) =>
    {
      var body = match.Groups["body"].Value;
      var sourceName = GetFieldValue(body, "name") ?? throw new InvalidOperationException("Moon entry is missing a name.");
      var displayName = NormalizeDisplayName(sourceName);
      var parentName = NormalizeDisplayName(GetFieldValue(body, "planet") ?? throw new InvalidOperationException($"Moon '{sourceName}' is missing a parent planet."));
      return CreateSeed(Slugify(displayName), displayName, "Moon", "moon", Slugify(parentName), 1000 + index, new
      {
        source = "solar_system.js",
        catalog = "MOON_DATA",
        sourceName,
        parent = parentName,
        kind = "moon"
      });
    });
  }

  private static IEnumerable<CatalogBodySeed> ParseDwarfs(string text)
  {
    return ParseInlineObjects(GetArrayBody(text, "const DWARF_DATA = ["), (match, index) =>
    {
      var body = match.Groups["body"].Value;
      var sourceName = GetFieldValue(body, "name") ?? throw new InvalidOperationException("Dwarf planet entry is missing a name.");
      var displayName = NormalizeDisplayName(sourceName);
      var type = GetFieldValue(body, "type") ?? "Dwarf Planet";
      return CreateSeed(Slugify(displayName), displayName, type, "dwarf-planet", "sun", 2000 + index, new
      {
        source = "solar_system.js",
        catalog = "DWARF_DATA",
        sourceName,
        kind = "dwarf-planet"
      });
    });
  }

  private static IEnumerable<CatalogBodySeed> ParseComets(string text)
  {
    return ParseInlineObjects(GetArrayBody(text, "const COMET_DATA = ["), (match, index) =>
    {
      var body = match.Groups["body"].Value;
      var sourceName = GetFieldValue(body, "name") ?? throw new InvalidOperationException("Comet entry is missing a name.");
      var displayName = NormalizeDisplayName(sourceName);
      return CreateSeed(Slugify(displayName), displayName, "Comet", "comet", "sun", 3000 + index, new
      {
        source = "solar_system.js",
        catalog = "COMET_DATA",
        sourceName,
        kind = "comet"
      });
    });
  }

  private static IEnumerable<CatalogBodySeed> ParseVoyagers(string text)
  {
    var arrayBody = GetArrayBody(text, "const VOYAGER_DATA = [");
    var matches = VoyagerNameRegex().Matches(arrayBody);
    for (var index = 0; index < matches.Count; index++) {
      var sourceName = matches[index].Groups["name"].Value;
      var displayName = NormalizeDisplayName(sourceName);
      yield return CreateSeed(Slugify(displayName), displayName, "Space Probe", "probe", null, 4000 + index, new
      {
        source = "voyager_trajectories.js",
        catalog = "VOYAGER_DATA",
        sourceName,
        kind = "probe"
      });
    }
  }

  private static IEnumerable<CatalogBodySeed> ParseInlineObjects(string arrayBody, Func<Match, int, CatalogBodySeed> projector)
  {
    var matches = InlineObjectRegex().Matches(arrayBody);
    for (var index = 0; index < matches.Count; index++) {
      yield return projector(matches[index], index);
    }
  }

  private static string GetArrayBody(string text, string marker)
  {
    var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0) throw new InvalidOperationException($"Could not find marker '{marker}'.");

    var arrayStart = text.IndexOf('[', markerIndex);
    var arrayEnd = text.IndexOf("];", arrayStart, StringComparison.Ordinal);
    if (arrayStart < 0 || arrayEnd < 0) throw new InvalidOperationException($"Could not parse array starting at '{marker}'.");

    return text[(arrayStart + 1)..arrayEnd];
  }

  private static string? GetFieldValue(string body, string fieldName)
  {
    var matches = FieldRegex().Matches(body);
    foreach (Match match in matches) {
      var group = match.Groups[fieldName];
      if (group.Success) return group.Value;
    }

    return null;
  }

  private static CatalogBodySeed CreateSeed(string slug, string displayName, string category, string kind, string? parentSlug, int sortOrder, object metadata)
  {
    return new CatalogBodySeed(
      slug,
      displayName,
      category,
      kind,
      parentSlug,
      sortOrder,
      JsonSerializer.Serialize(metadata)
    );
  }

  private static string NormalizeDisplayName(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return value;
    var trimmed = value.Trim();
    if (!trimmed.Any(char.IsLetter)) return trimmed;
    if (trimmed.Any(char.IsLower)) return trimmed;
    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
  }

  private static string Slugify(string value)
  {
    var normalized = value.Trim().ToLowerInvariant();
    normalized = normalized.Replace("'", string.Empty, StringComparison.Ordinal);
    normalized = normalized.Replace("/", "-", StringComparison.Ordinal);
    normalized = normalized.Replace(" ", "-", StringComparison.Ordinal);
    normalized = SlugCleanupRegex().Replace(normalized, "-");
    normalized = normalized.Trim('-');
    return normalized;
  }

  [GeneratedRegex("\\{(?<body>[^\\r\\n]*name\\s*:\\s*['\"](?<name>[^'\"]+)['\"][^\\r\\n]*)\\}", RegexOptions.Compiled)]
  private static partial Regex InlineObjectRegex();

  [GeneratedRegex("planet\\s*:\\s*['\"](?<planet>[^'\"]+)['\"]|type\\s*:\\s*['\"](?<type>[^'\"]+)['\"]|name\\s*:\\s*['\"](?<name>[^'\"]+)['\"]", RegexOptions.Compiled)]
  private static partial Regex FieldRegex();

  [GeneratedRegex("name\\s*:\\s*['\"](?<name>[^'\"]+)['\"]", RegexOptions.Compiled)]
  private static partial Regex VoyagerNameRegex();

  [GeneratedRegex(@"[^a-z0-9-]+", RegexOptions.Compiled)]
  private static partial Regex SlugCleanupRegex();
}