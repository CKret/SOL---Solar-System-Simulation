using System.Globalization;

namespace Sol.Api.Services;

// Converts between calendar dates and Julian Day Numbers (Meeus, Astronomical Algorithms Ch. 7).
// Julian calendar is used for dates before 1582-Oct-15; Gregorian after.
// Astronomical year numbering: 1 BC = year 0, 2 BC = year -1, etc.
public static class JulianDateConverter
{
  public static double FromCalendar(int year, int month, double day)
  {
    if (month <= 2) {
      year -= 1;
      month += 12;
    }

    double b = 0;
    if (year > 1582 || (year == 1582 && (month > 10 || (month == 10 && day >= 15)))) {
      double a = Math.Floor((double)year / 100);
      b = 2 - a + Math.Floor(a / 4);
    }

    return Math.Floor(365.25 * (year + 4716))
         + Math.Floor(30.6001 * (month + 1))
         + day + b - 1524.5;
  }

  public static double FromDateTime(DateTime dt)
  {
    double dayFrac = dt.Day + (dt.Hour + dt.Minute / 60.0 + dt.Second / 3600.0 + dt.Millisecond / 3600000.0) / 24.0;
    return FromCalendar(dt.Year, dt.Month, dayFrac);
  }

  public static (int Year, int Month, int Day, int Hour, int Minute, double Second) ToCalendar(double jd)
  {
    double jdHalf = jd + 0.5;
    double z = Math.Floor(jdHalf);
    double f = jdHalf - z;

    double a;
    if (z < 2299161) {
      a = z;
    } else {
      double alpha = Math.Floor((z - 1867216.25) / 36524.25);
      a = z + 1 + alpha - Math.Floor(alpha / 4);
    }

    double b = a + 1524;
    double c = Math.Floor((b - 122.1) / 365.25);
    double d = Math.Floor(365.25 * c);
    double e = Math.Floor((b - d) / 30.6001);

    double dayWithFrac = b - d - Math.Floor(30.6001 * e) + f;
    int day = (int)Math.Floor(dayWithFrac);
    double timeFrac = dayWithFrac - day;

    int month = e < 14 ? (int)(e - 1) : (int)(e - 13);
    int year = month > 2 ? (int)(c - 4716) : (int)(c - 4715);

    double totalSecs = timeFrac * 86400.0;
    int hour = (int)Math.Floor(totalSecs / 3600);
    totalSecs -= hour * 3600;
    int minute = (int)Math.Floor(totalSecs / 60);
    double second = totalSecs - minute * 60;

    return (year, month, day, hour, minute, second);
  }

  // Parses Horizons output timestamp: "A.D. yyyy-MMM-dd HH:mm:ss.ffff" or "B.C. yyyy-MMM-dd HH:mm:ss.ffff"
  // Components are parsed manually to avoid Gregorian calendar validation — Horizons returns Julian
  // calendar dates for far-past/far-future ranges, which can include days like Feb 29 in years that
  // are not Gregorian leap years.
  public static double ParseHorizonsTimestamp(string s)
  {
    s = s.Trim();
    bool bc = s.StartsWith("B.C.", StringComparison.Ordinal);
    if (!bc && !s.StartsWith("A.D.", StringComparison.Ordinal))
      throw new FormatException($"Unrecognized Horizons timestamp: '{s}'");

    // "yyyy-MMM-dd HH:mm:ss.ffff"
    var d = s[5..].Trim();
    int y1 = d.IndexOf('-');
    int y2 = d.IndexOf('-', y1 + 1);
    int calYear  = int.Parse(d[..y1], CultureInfo.InvariantCulture);
    int month    = ParseMonthAbbr(d[(y1 + 1)..y2]);
    int day      = int.Parse(d[(y2 + 1)..(y2 + 3)], CultureInfo.InvariantCulture);
    int hour     = int.Parse(d[(y2 + 4)..(y2 + 6)], CultureInfo.InvariantCulture);
    int minute   = int.Parse(d[(y2 + 7)..(y2 + 9)], CultureInfo.InvariantCulture);
    double sec   = double.Parse(d[(y2 + 10)..], NumberStyles.Float, CultureInfo.InvariantCulture);

    double dayFrac = day + (hour + minute / 60.0 + sec / 3600.0) / 24.0;
    // BC 1 = astronomical year 0, BC 2 = year -1, etc.
    int year = bc ? 1 - calYear : calYear;
    return FromCalendar(year, month, dayFrac);
  }

  private static int ParseMonthAbbr(string abbr) => abbr switch
  {
    "Jan" => 1, "Feb" => 2, "Mar" => 3, "Apr" => 4,
    "May" => 5, "Jun" => 6, "Jul" => 7, "Aug" => 8,
    "Sep" => 9, "Oct" => 10, "Nov" => 11, "Dec" => 12,
    _ => throw new FormatException($"Unknown month abbreviation: '{abbr}'")
  };

  // Formats a JD for Horizons API input (without surrounding single quotes).
  // AD: "2000-Jan-01 12:00" — BC: "BC 0100-Jan-01 00:00"
  public static string ToHorizonsDateString(double jd)
  {
    var (year, month, day, hour, minute, _) = ToCalendar(jd);
    string mon = MonthAbbr(month);
    if (year <= 0) {
      int bcYear = 1 - year;
      return $"BC {bcYear:D4}-{mon}-{day:D2} {hour:D2}:{minute:D2}";
    }
    return $"{year:D4}-{mon}-{day:D2} {hour:D2}:{minute:D2}";
  }

  private static string MonthAbbr(int month) => month switch
  {
    1 => "Jan", 2 => "Feb", 3 => "Mar", 4 => "Apr",
    5 => "May", 6 => "Jun", 7 => "Jul", 8 => "Aug",
    9 => "Sep", 10 => "Oct", 11 => "Nov", 12 => "Dec",
    _ => throw new ArgumentOutOfRangeException(nameof(month))
  };
}
