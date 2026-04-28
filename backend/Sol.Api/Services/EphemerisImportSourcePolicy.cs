namespace Sol.Api.Services;

public static class EphemerisImportSourcePolicy
{
  public static TimeSpan GetBaseSampleRate(string slug)
  {
    return slug.ToLowerInvariant() switch
    {
      "sun" => TimeSpan.FromDays(1),
      "mercury" => TimeSpan.FromDays(1),
      "venus" => TimeSpan.FromDays(1),
      "earth" => TimeSpan.FromDays(1),
      "mars" => TimeSpan.FromDays(1),
      "jupiter" => TimeSpan.FromDays(1),
      "saturn" => TimeSpan.FromDays(1),
      "uranus" => TimeSpan.FromDays(1),
      "neptune" => TimeSpan.FromDays(1),
      "ceres" => TimeSpan.FromDays(1),
      "pluto" => TimeSpan.FromDays(1),
      "eris" => TimeSpan.FromDays(1),
      "makemake" => TimeSpan.FromDays(1),
      "haumea" => TimeSpan.FromDays(1),
      "sedna" => TimeSpan.FromDays(1),
      "gonggong" => TimeSpan.FromDays(1),
      "quaoar" => TimeSpan.FromDays(1),
      "orcus" => TimeSpan.FromDays(1),
      "halley" => TimeSpan.FromDays(1),
      "hale-bopp" => TimeSpan.FromDays(1),
      "hyakutake" => TimeSpan.FromDays(1),
      "encke" => TimeSpan.FromDays(1),
      "67p-churyumov-gerasimenko" => TimeSpan.FromDays(1),
      "tempel-1" => TimeSpan.FromDays(1),
      "wild-2" => TimeSpan.FromDays(1),
      "shoemaker-levy-9" => TimeSpan.FromDays(1),
      "neowise" => TimeSpan.FromDays(1),
      "ikeya-seki" => TimeSpan.FromDays(1),
      "voyager-1" => TimeSpan.FromDays(1),
      "voyager-2" => TimeSpan.FromDays(1),
      _ => TimeSpan.FromDays(1)
    };
  }

  public static bool IsVoyager(string slug)
  {
    return string.Equals(slug, "voyager-1", StringComparison.OrdinalIgnoreCase)
      || string.Equals(slug, "voyager-2", StringComparison.OrdinalIgnoreCase);
  }

  public static IReadOnlyList<EphemerisImportWindow> GetWindowsForTarget(string slug, DateTime startUtc, DateTime endUtc, TimeSpan? sampleRateOverride)
  {
    var baseStep = sampleRateOverride ?? GetBaseSampleRate(slug);
    var windows = new List<EphemerisImportWindow>
    {
      new(startUtc, endUtc, baseStep, "default")
    };

    if (!IsVoyager(slug)) {
      return windows;
    }

    foreach (var encounter in VoyagerHourlyEncounterWindows.Where(encounter => string.Equals(encounter.Slug, slug, StringComparison.OrdinalIgnoreCase))) {
      var overlapStart = startUtc > encounter.StartUtc ? startUtc : encounter.StartUtc;
      var overlapEnd = endUtc < encounter.EndUtc ? endUtc : encounter.EndUtc;
      if (overlapStart > overlapEnd) {
        continue;
      }

      windows.Add(new EphemerisImportWindow(overlapStart, overlapEnd, TimeSpan.FromHours(1), encounter.Label));
    }

    return windows;
  }

  private static readonly VoyagerEncounterWindow[] VoyagerHourlyEncounterWindows =
  [
    new("voyager-1", new DateTime(1979, 3, 4, 0, 0, 0, DateTimeKind.Utc), new DateTime(1979, 3, 8, 0, 0, 0, DateTimeKind.Utc), "jupiter-encounter"),
    new("voyager-1", new DateTime(1980, 11, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(1980, 11, 14, 0, 0, 0, DateTimeKind.Utc), "saturn-encounter"),
    new("voyager-2", new DateTime(1979, 7, 8, 0, 0, 0, DateTimeKind.Utc), new DateTime(1979, 7, 12, 0, 0, 0, DateTimeKind.Utc), "jupiter-encounter"),
    new("voyager-2", new DateTime(1981, 8, 24, 0, 0, 0, DateTimeKind.Utc), new DateTime(1981, 8, 28, 0, 0, 0, DateTimeKind.Utc), "saturn-encounter"),
    new("voyager-2", new DateTime(1986, 1, 23, 0, 0, 0, DateTimeKind.Utc), new DateTime(1986, 1, 27, 0, 0, 0, DateTimeKind.Utc), "uranus-encounter"),
    new("voyager-2", new DateTime(1989, 8, 24, 0, 0, 0, DateTimeKind.Utc), new DateTime(1989, 8, 28, 0, 0, 0, DateTimeKind.Utc), "neptune-encounter")
  ];
}

public sealed record EphemerisImportWindow(DateTime StartUtc, DateTime EndUtc, TimeSpan Step, string Reason);
internal sealed record VoyagerEncounterWindow(string Slug, DateTime StartUtc, DateTime EndUtc, string Label);