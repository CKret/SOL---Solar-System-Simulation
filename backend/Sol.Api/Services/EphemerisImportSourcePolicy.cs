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
      "voyager-1"         => TimeSpan.FromDays(1),
      "voyager-2"         => TimeSpan.FromDays(1),
      "cassini"           => TimeSpan.FromDays(1),
      "pioneer-10"        => TimeSpan.FromDays(1),
      "pioneer-11"        => TimeSpan.FromDays(1),
      "new-horizons"      => TimeSpan.FromDays(1),
      "juno"              => TimeSpan.FromDays(1),
      "parker-solar-probe"=> TimeSpan.FromDays(1),
      "bepicolombo"       => TimeSpan.FromDays(1),
      "galileo"           => TimeSpan.FromDays(1),
      "messenger"         => TimeSpan.FromDays(1),
      "dawn"              => TimeSpan.FromDays(1),
      "rosetta"           => TimeSpan.FromDays(1),
      "osiris-rex"        => TimeSpan.FromDays(1),
      _ => TimeSpan.FromDays(1)
    };
  }

  public static IReadOnlyList<EphemerisImportWindow> GetWindowsForTarget(string slug, double startJd, double endJd, TimeSpan? sampleRateOverride)
  {
    var baseStep = sampleRateOverride ?? GetBaseSampleRate(slug);
    var windows = new List<EphemerisImportWindow>
    {
      new(startJd, endJd, baseStep, "default")
    };

    foreach (var encounter in HourlyEncounterWindows.Where(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase))) {
      var overlapStart = startJd > encounter.StartJd ? startJd : encounter.StartJd;
      var overlapEnd = endJd < encounter.EndJd ? endJd : encounter.EndJd;
      if (overlapStart > overlapEnd) {
        continue;
      }

      windows.Add(new EphemerisImportWindow(overlapStart, overlapEnd, TimeSpan.FromHours(1), encounter.Label));
    }

    return windows;
  }

  private static readonly ProbeEncounterWindow[] HourlyEncounterWindows =
  [
    new("voyager-1", JulianDateConverter.FromCalendar(1979, 3,  4.0),  JulianDateConverter.FromCalendar(1979, 3,  8.0),  "jupiter-encounter"),
    new("voyager-1", JulianDateConverter.FromCalendar(1980, 11, 10.0), JulianDateConverter.FromCalendar(1980, 11, 14.0), "saturn-encounter"),
    new("voyager-2", JulianDateConverter.FromCalendar(1979, 7,  8.0),  JulianDateConverter.FromCalendar(1979, 7,  12.0), "jupiter-encounter"),
    new("voyager-2", JulianDateConverter.FromCalendar(1981, 8,  24.0), JulianDateConverter.FromCalendar(1981, 8,  28.0), "saturn-encounter"),
    new("voyager-2", JulianDateConverter.FromCalendar(1986, 1,  23.0), JulianDateConverter.FromCalendar(1986, 1,  27.0), "uranus-encounter"),
    new("voyager-2", JulianDateConverter.FromCalendar(1989, 8,  24.0), JulianDateConverter.FromCalendar(1989, 8,  28.0), "neptune-encounter"),

    new("cassini",          JulianDateConverter.FromCalendar(2000, 12, 28.0), JulianDateConverter.FromCalendar(2001, 1,   1.0),  "jupiter-encounter"),
    new("cassini",          JulianDateConverter.FromCalendar(2004, 6,  29.0), JulianDateConverter.FromCalendar(2004, 7,   3.0),  "saturn-orbit-insertion"),
    new("cassini",          JulianDateConverter.FromCalendar(2005, 1,  12.0), JulianDateConverter.FromCalendar(2005, 1,  16.0),  "huygens-titan"),
    new("cassini",          JulianDateConverter.FromCalendar(2005, 2,  15.0), JulianDateConverter.FromCalendar(2005, 2,  19.0),  "enceladus-e1"),
    new("cassini",          JulianDateConverter.FromCalendar(2006, 3,  11.0), JulianDateConverter.FromCalendar(2006, 3,  15.0),  "saturn-mimas-janus-tethys"),
    new("cassini",          JulianDateConverter.FromCalendar(2017, 9,  13.0), JulianDateConverter.FromCalendar(2017, 9,  15.0),  "grand-finale"),

    new("pioneer-10",       JulianDateConverter.FromCalendar(1973, 12,  1.0), JulianDateConverter.FromCalendar(1973, 12,  5.0),  "jupiter-encounter"),
    new("pioneer-11",       JulianDateConverter.FromCalendar(1974, 11, 30.0), JulianDateConverter.FromCalendar(1974, 12,  6.0),  "jupiter-encounter"),
    new("pioneer-11",       JulianDateConverter.FromCalendar(1979, 8,  30.0), JulianDateConverter.FromCalendar(1979, 9,   3.0),  "saturn-encounter"),

    new("new-horizons",     JulianDateConverter.FromCalendar(2007, 2,  26.0), JulianDateConverter.FromCalendar(2007, 3,   2.0),  "jupiter-encounter"),
    new("new-horizons",     JulianDateConverter.FromCalendar(2015, 7,  12.0), JulianDateConverter.FromCalendar(2015, 7,  16.0),  "pluto-encounter"),
    new("new-horizons",     JulianDateConverter.FromCalendar(2018, 12, 30.0), JulianDateConverter.FromCalendar(2019, 1,   3.0),  "arrokoth-encounter"),

    new("juno",             JulianDateConverter.FromCalendar(2016, 7,   2.0), JulianDateConverter.FromCalendar(2016, 7,   6.0),  "jupiter-orbit-insertion"),

    new("parker-solar-probe",JulianDateConverter.FromCalendar(2018, 11,  3.0), JulianDateConverter.FromCalendar(2018, 11,  7.0), "perihelion-1"),
    new("parker-solar-probe",JulianDateConverter.FromCalendar(2019, 4,   2.0), JulianDateConverter.FromCalendar(2019, 4,   6.0), "perihelion-2"),
    new("parker-solar-probe",JulianDateConverter.FromCalendar(2024, 12, 22.0), JulianDateConverter.FromCalendar(2024, 12, 26.0), "perihelion-closest"),

    new("bepicolombo",      JulianDateConverter.FromCalendar(2020, 4,   8.0), JulianDateConverter.FromCalendar(2020, 4,  12.0),  "earth-flyby"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2020, 10, 13.0), JulianDateConverter.FromCalendar(2020, 10, 17.0),  "venus-flyby-1"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2021, 8,   8.0), JulianDateConverter.FromCalendar(2021, 8,  12.0),  "venus-flyby-2"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2021, 9,  29.0), JulianDateConverter.FromCalendar(2021, 10,  3.0),  "mercury-flyby-1"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2022, 6,  21.0), JulianDateConverter.FromCalendar(2022, 6,  25.0),  "mercury-flyby-2"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2023, 6,  17.0), JulianDateConverter.FromCalendar(2023, 6,  21.0),  "mercury-flyby-3"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2024, 9,   3.0), JulianDateConverter.FromCalendar(2024, 9,   7.0),  "mercury-flyby-4"),
    new("bepicolombo",      JulianDateConverter.FromCalendar(2024, 11, 29.0), JulianDateConverter.FromCalendar(2024, 12,  3.0),  "mercury-flyby-5"),

    new("galileo",          JulianDateConverter.FromCalendar(1990, 2,   8.0), JulianDateConverter.FromCalendar(1990, 2,  12.0),  "venus-flyby"),
    new("galileo",          JulianDateConverter.FromCalendar(1990, 12,  6.0), JulianDateConverter.FromCalendar(1990, 12, 10.0),  "earth-flyby-1"),
    new("galileo",          JulianDateConverter.FromCalendar(1991, 10, 27.0), JulianDateConverter.FromCalendar(1991, 10, 31.0),  "gaspra-flyby"),
    new("galileo",          JulianDateConverter.FromCalendar(1992, 12,  6.0), JulianDateConverter.FromCalendar(1992, 12, 10.0),  "earth-flyby-2"),
    new("galileo",          JulianDateConverter.FromCalendar(1993, 8,  26.0), JulianDateConverter.FromCalendar(1993, 8,  30.0),  "ida-flyby"),
    new("galileo",          JulianDateConverter.FromCalendar(1994, 7,  14.0), JulianDateConverter.FromCalendar(1994, 7,  24.0),  "shoemaker-levy-9-impacts"),
    new("galileo",          JulianDateConverter.FromCalendar(1995, 12,  5.0), JulianDateConverter.FromCalendar(1995, 12,  9.0),  "jupiter-orbit-insertion"),

    new("messenger",        JulianDateConverter.FromCalendar(2005, 7,  31.0), JulianDateConverter.FromCalendar(2005, 8,   4.0),  "earth-flyby"),
    new("messenger",        JulianDateConverter.FromCalendar(2006, 10, 22.0), JulianDateConverter.FromCalendar(2006, 10, 26.0),  "venus-flyby-1"),
    new("messenger",        JulianDateConverter.FromCalendar(2007, 6,   3.0), JulianDateConverter.FromCalendar(2007, 6,   7.0),  "venus-flyby-2"),
    new("messenger",        JulianDateConverter.FromCalendar(2008, 1,  12.0), JulianDateConverter.FromCalendar(2008, 1,  16.0),  "mercury-flyby-1"),
    new("messenger",        JulianDateConverter.FromCalendar(2008, 10,  4.0), JulianDateConverter.FromCalendar(2008, 10,  8.0),  "mercury-flyby-2"),
    new("messenger",        JulianDateConverter.FromCalendar(2009, 9,  27.0), JulianDateConverter.FromCalendar(2009, 10,  1.0),  "mercury-flyby-3"),
    new("messenger",        JulianDateConverter.FromCalendar(2011, 3,  16.0), JulianDateConverter.FromCalendar(2011, 3,  20.0),  "mercury-orbit-insertion"),
    new("messenger",        JulianDateConverter.FromCalendar(2015, 4,  28.0), JulianDateConverter.FromCalendar(2015, 5,   2.0),  "end-of-mission"),

    new("dawn",             JulianDateConverter.FromCalendar(2009, 2,  15.0), JulianDateConverter.FromCalendar(2009, 2,  19.0),  "mars-flyby"),
    new("dawn",             JulianDateConverter.FromCalendar(2011, 7,  14.0), JulianDateConverter.FromCalendar(2011, 7,  18.0),  "vesta-arrival"),
    new("dawn",             JulianDateConverter.FromCalendar(2012, 9,   3.0), JulianDateConverter.FromCalendar(2012, 9,   7.0),  "vesta-departure"),
    new("dawn",             JulianDateConverter.FromCalendar(2015, 3,   4.0), JulianDateConverter.FromCalendar(2015, 3,   8.0),  "ceres-arrival"),

    new("rosetta",          JulianDateConverter.FromCalendar(2014, 8,   4.0), JulianDateConverter.FromCalendar(2014, 8,   8.0),  "67p-arrival"),
    new("rosetta",          JulianDateConverter.FromCalendar(2014, 11, 10.0), JulianDateConverter.FromCalendar(2014, 11, 14.0),  "philae-landing"),
    new("rosetta",          JulianDateConverter.FromCalendar(2016, 9,  28.0), JulianDateConverter.FromCalendar(2016, 10,  2.0),  "end-of-mission"),

    new("osiris-rex",       JulianDateConverter.FromCalendar(2018, 12,  1.0), JulianDateConverter.FromCalendar(2018, 12,  5.0),  "bennu-arrival"),
    new("osiris-rex",       JulianDateConverter.FromCalendar(2020, 10, 18.0), JulianDateConverter.FromCalendar(2020, 10, 22.0),  "sample-collection"),
    new("osiris-rex",       JulianDateConverter.FromCalendar(2023, 9,  22.0), JulianDateConverter.FromCalendar(2023, 9,  26.0),  "sample-return"),
  ];
}

public sealed record EphemerisImportWindow(double StartJd, double EndJd, TimeSpan Step, string Reason);
internal sealed record ProbeEncounterWindow(string Slug, double StartJd, double EndJd, string Label);
