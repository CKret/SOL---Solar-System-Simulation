using Microsoft.Data.SqlClient;
using Sol.Api.Models;
using System.Data;
using System.Text;

namespace Sol.Api.Services;

public sealed class SqlServerEphemerisRepository(ISqlConnectionFactory connectionFactory) : IEphemerisRepository
{
  private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;

  private const string BodyColumns = @"
    b.BodyId         AS Id,
    COALESCE(NULLIF(LTRIM(RTRIM(b.Slug)), ''), CONCAT('body-', b.BodyId)) AS Slug,
    COALESCE(NULLIF(LTRIM(RTRIM(b.DisplayName)), ''), NULLIF(LTRIM(RTRIM(b.Slug)), ''), CONCAT('body-', b.BodyId)) AS Name,
    COALESCE(NULLIF(LTRIM(RTRIM(b.Kind)), ''), 'small-body') AS Kind,
    b.ParentBodyId,
    COALESCE(b.SortOrder, 2147483647) AS SortOrder,
    b.JplHorizonsId,
    b.SbdbDesig,
    b.H_AbsMag,
    COALESCE(b.HasEphemeris, CAST(0 AS bit)) AS HasEphemeris,
    b.EphemerisMinJD,
    b.EphemerisMaxJD,
    b.EphemerisMinStr,
    b.EphemerisMaxStr,
    b.Eccentricity,
    b.Perihelion_AU,
    b.Aphelion_AU,
    b.Inclination_deg,
    b.LongAscNode_deg,
    b.ArgPerihelion_deg,
    b.SemiMajorAxis_AU,
    b.MeanAnomaly_deg,
    b.MeanMotion_degPerDay,
    b.OrbitalPeriod_days,
    b.Epoch_JD,
    b.T_Perihelion_JD,
    b.GM_km3s2,
    b.MeanRadius_km,
    b.EquatorialRadius_km,
    b.Mass_1e23kg";

  public async Task<IReadOnlyList<BodySummary>> GetBodiesAsync(double? hMax, int? maxBodies, CancellationToken cancellationToken)
  {
    var sql = new StringBuilder("SELECT");

    if (maxBodies.HasValue)
      sql.Append(" TOP (@maxBodies)");

    sql.Append(BodyColumns)
      .Append(" FROM dbo.Bodies b WHERE b.IsActive = 1");

    if (hMax.HasValue)
      sql.Append(" AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMax)");
    else sql.Append(" AND b.Source != 'mpcorb' AND b.Kind IN ('star', 'planet', 'probe', 'moon', 'dwarf-planet', 'comet')"); // When no hMax, exclude non-authoritative MPCORB bodies which can have H_AbsMag IS NULL.

    sql.Append(" ORDER BY b.SortOrder, b.DisplayName;");

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql.ToString(), connection) { CommandTimeout = 0 };
    if (hMax.HasValue) command.Parameters.AddWithValue("@hMax", hMax.Value);
    if (maxBodies.HasValue) command.Parameters.AddWithValue("@maxBodies", Math.Max(1, maxBodies.Value));
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

    var results = new List<BodySummary>();
    while (await reader.ReadAsync(cancellationToken))
      results.Add(ReadBodySummary(reader));
    return results;
  }

  public async Task<IReadOnlyList<BodySummary>> GetBodiesBatchAsync(
      double? hMinExclusive, double? hMaxInclusive, int take, int? afterBodyId, CancellationToken cancellationToken)
  {
    var sql = new StringBuilder("SELECT TOP (@take)")
      .Append(BodyColumns)
      .Append(" FROM dbo.Bodies b WHERE b.IsActive = 1");

    if (afterBodyId.HasValue)
      sql.Append(" AND b.BodyId > @afterBodyId");

    if (hMaxInclusive.HasValue)
    {
      if (hMinExclusive.HasValue)
      {
        // Incremental H-step range: include only newly unlocked magnitude band.
        sql.Append(" AND b.H_AbsMag IS NOT NULL AND b.H_AbsMag > @hMinExclusive AND b.H_AbsMag <= @hMaxInclusive");
      }
      else
      {
        // Initial pass: include authoritative bodies (H NULL) + bodies within threshold.
        sql.Append(" AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMaxInclusive)");
      }
    }
    else
    {
      sql.Append(" AND b.Source != 'mpcorb'");
    }

    sql.Append(" ORDER BY b.BodyId;");

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql.ToString(), connection) { CommandTimeout = 0 };
    command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 50_000));
    if (afterBodyId.HasValue) command.Parameters.AddWithValue("@afterBodyId", afterBodyId.Value);
    if (hMinExclusive.HasValue) command.Parameters.AddWithValue("@hMinExclusive", hMinExclusive.Value);
    if (hMaxInclusive.HasValue) command.Parameters.AddWithValue("@hMaxInclusive", hMaxInclusive.Value);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
    var results = new List<BodySummary>();
    while (await reader.ReadAsync(cancellationToken))
      results.Add(ReadBodySummary(reader));
    return results;
  }

  public async Task<IReadOnlyList<BodySummary>> SearchBodiesAsync(
      string? query,
      int limit,
      bool completedEphemerisOnly,
      bool namedOnly,
      CancellationToken cancellationToken)
  {
    var sql = new StringBuilder("SELECT TOP (@limit)")
      .Append(BodyColumns)
      .Append(" FROM dbo.Bodies b WHERE b.IsActive = 1");

    if (completedEphemerisOnly)
      sql.Append(" AND b.CompletedEphemeris = 1");

    if (namedOnly)
    {
      sql.Append(@" AND b.DisplayName IS NOT NULL
                    AND LEN(LTRIM(RTRIM(b.DisplayName))) > 0
                    AND LTRIM(RTRIM(b.DisplayName)) NOT LIKE '[0-9]%' ");
    }

    var trimmed = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    if (trimmed is not null)
    {
      sql.Append(@" AND (
          b.DisplayName LIKE @contains
          OR b.Slug LIKE @contains
          OR b.SbdbDesig LIKE @contains
          OR b.JplHorizonsId LIKE @contains
      )");

      sql.Append(@" ORDER BY
        CASE WHEN b.DisplayName LIKE @prefix THEN 0 ELSE 1 END,
        CASE WHEN b.Slug LIKE @prefix THEN 0 ELSE 1 END,
        b.SortOrder,
        b.DisplayName;");
    }
    else
    {
      sql.Append(" ORDER BY b.SortOrder, b.DisplayName;");
    }

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql.ToString(), connection);
    command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 2000));
    if (trimmed is not null)
    {
      command.Parameters.AddWithValue("@contains", $"%{trimmed}%");
      command.Parameters.AddWithValue("@prefix", $"{trimmed}%");
    }

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
    var results = new List<BodySummary>();
    while (await reader.ReadAsync(cancellationToken))
      results.Add(ReadBodySummary(reader));
    return results;
  }

  public async Task<BodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken)
  {
    var sql = "SELECT" + BodyColumns +
      " FROM dbo.Bodies b WHERE b.IsActive = 1 AND b.Slug = @slug;";

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@slug", slug);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

    if (!await reader.ReadAsync(cancellationToken)) return null;
    return ReadBodySummary(reader);
  }

  public async Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken)
  {
    double startJd = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
    double endJd   = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc,   DateTimeKind.Utc));

    const string sql = @"
SELECT TOP (@limit) BodyId, SampleJd,
  X_AU AS X, Y_AU AS Y, Z_AU AS Z,
  VX_AUPerDay AS Vx, VY_AUPerDay AS Vy, VZ_AUPerDay AS Vz,
  Frame
FROM dbo.EphemerisSamples
WHERE BodyId = @bodyId
  AND SampleJd >= @startJd
  AND SampleJd <= @endJd
ORDER BY SampleJd;";

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@bodyId",  bodyId);
    command.Parameters.AddWithValue("@startJd", startJd);
    command.Parameters.AddWithValue("@endJd",   endJd);
    command.Parameters.AddWithValue("@limit",   limit);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

    var results = new List<EphemerisSample>();
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new EphemerisSample(
        BodyId:   GetInt32(reader, "BodyId"),
        SampleJd: GetDouble(reader, "SampleJd"),
        X:  GetDouble(reader, "X"),
        Y:  GetDouble(reader, "Y"),
        Z:  GetDouble(reader, "Z"),
        Vx: GetNullableDouble(reader, "Vx"),
        Vy: GetNullableDouble(reader, "Vy"),
        Vz: GetNullableDouble(reader, "Vz"),
        Frame: GetNullableString(reader, "Frame")));
    }
    return results;
  }

      public async Task<IReadOnlyList<EphemerisSample>> GetBulkSamplesAsync(
        DateTime startUtc, DateTime endUtc, double? hMax, int step, int? maxBodies, CancellationToken cancellationToken)
  {
    double startJd = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
    double endJd   = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc,   DateTimeKind.Utc));

    // Include bodies with NULL H_AbsMag (authoritative bodies: planets, moons, etc.)
    // and bodies with H_AbsMag <= hMax. When hMax is null, restrict to authoritative kinds only.
    var hFilter = hMax.HasValue
      ? "AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMax)"
      : "AND b.Source != 'mpcorb' AND b.Kind IN ('star', 'planet', 'probe', 'moon', 'dwarf-planet', 'comet')";

    // step=1 returns every sample; step=N picks one sample per N days (ROW_NUMBER per body).
    var sql = step <= 1 ? $@"
WITH selectedBodies AS (
  SELECT TOP (@maxBodies)
    b.BodyId
  FROM dbo.Bodies b
  WHERE b.IsActive = 1
    AND b.HasEphemeris = 1
    {hFilter}
  ORDER BY
    CASE WHEN b.Source = 'mpcorb' THEN 1 ELSE 0 END,
    COALESCE(b.SortOrder, 2147483647),
    CASE WHEN b.H_AbsMag IS NULL THEN 0 ELSE 1 END,
    ISNULL(b.H_AbsMag, 0),
    b.BodyId
)
SELECT e.BodyId, e.SampleJd,
  e.X_AU AS X, e.Y_AU AS Y, e.Z_AU AS Z,
  e.VX_AUPerDay AS Vx, e.VY_AUPerDay AS Vy, e.VZ_AUPerDay AS Vz
FROM dbo.EphemerisSamples e
INNER JOIN selectedBodies sb ON sb.BodyId = e.BodyId
WHERE e.SampleJd >= @startJd
  AND e.SampleJd <= @endJd
ORDER BY e.SampleJd, e.BodyId;" : $@"
WITH selectedBodies AS (
  SELECT TOP (@maxBodies)
    b.BodyId
  FROM dbo.Bodies b
  WHERE b.IsActive = 1
    AND b.HasEphemeris = 1
    {hFilter}
  ORDER BY
    CASE WHEN b.Source = 'mpcorb' THEN 1 ELSE 0 END,
    COALESCE(b.SortOrder, 2147483647),
    CASE WHEN b.H_AbsMag IS NULL THEN 0 ELSE 1 END,
    ISNULL(b.H_AbsMag, 0),
    b.BodyId
),
ranked AS (
  SELECT e.BodyId, e.SampleJd,
    e.X_AU AS X, e.Y_AU AS Y, e.Z_AU AS Z,
    e.VX_AUPerDay AS Vx, e.VY_AUPerDay AS Vy, e.VZ_AUPerDay AS Vz,
    ROW_NUMBER() OVER (PARTITION BY e.BodyId ORDER BY e.SampleJd) AS rn
  FROM dbo.EphemerisSamples e
  INNER JOIN selectedBodies sb ON sb.BodyId = e.BodyId
  WHERE e.SampleJd >= @startJd
    AND e.SampleJd <= @endJd
)
SELECT BodyId, SampleJd, X, Y, Z, Vx, Vy, Vz
FROM ranked
WHERE rn % @step = 1
ORDER BY SampleJd, BodyId;";

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    command.Parameters.AddWithValue("@startJd", startJd);
    command.Parameters.AddWithValue("@endJd",   endJd);
    command.Parameters.AddWithValue("@maxBodies", Math.Max(1, maxBodies ?? int.MaxValue));
    if (hMax.HasValue) command.Parameters.AddWithValue("@hMax", hMax.Value);
    if (step > 1)      command.Parameters.AddWithValue("@step", step);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

    var results = new List<EphemerisSample>();
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new EphemerisSample(
        BodyId:   GetInt32(reader, "BodyId"),
        SampleJd: GetDouble(reader, "SampleJd"),
        X:  GetDouble(reader, "X"),
        Y:  GetDouble(reader, "Y"),
        Z:  GetDouble(reader, "Z"),
        Vx: GetNullableDouble(reader, "Vx"),
        Vy: GetNullableDouble(reader, "Vy"),
        Vz: GetNullableDouble(reader, "Vz"),
        Frame: null));
    }
    return results;
  }

  private static BodySummary ReadBodySummary(SqlDataReader r) => new(
    Id:                  GetInt32(r,  "Id"),
    Slug:                GetString(r, "Slug"),
    Name:                GetString(r, "Name"),
    Kind:                GetString(r, "Kind"),
    ParentBodyId:        GetNullableInt32(r,  "ParentBodyId"),
    SortOrder:           GetInt32(r,  "SortOrder"),
    JplHorizonsId:       GetNullableString(r, "JplHorizonsId"),
    SbdbDesig:           GetNullableString(r, "SbdbDesig"),
    H_AbsMag:            GetNullableDouble(r, "H_AbsMag"),
    HasEphemeris:        r.GetBoolean(r.GetOrdinal("HasEphemeris")),
    EphemerisMinJD:      GetNullableDouble(r, "EphemerisMinJD"),
    EphemerisMaxJD:      GetNullableDouble(r, "EphemerisMaxJD"),
    EphemerisMinStr:     GetNullableString(r, "EphemerisMinStr"),
    EphemerisMaxStr:     GetNullableString(r, "EphemerisMaxStr"),
    Eccentricity:        GetNullableDouble(r, "Eccentricity"),
    Perihelion_AU:       GetNullableDouble(r, "Perihelion_AU"),
    Aphelion_AU:         GetNullableDouble(r, "Aphelion_AU"),
    Inclination_deg:     GetNullableDouble(r, "Inclination_deg"),
    LongAscNode_deg:     GetNullableDouble(r, "LongAscNode_deg"),
    ArgPerihelion_deg:   GetNullableDouble(r, "ArgPerihelion_deg"),
    SemiMajorAxis_AU:    GetNullableDouble(r, "SemiMajorAxis_AU"),
    MeanAnomaly_deg:     GetNullableDouble(r, "MeanAnomaly_deg"),
    MeanMotion_degPerDay:GetNullableDouble(r, "MeanMotion_degPerDay"),
    OrbitalPeriod_days:  GetNullableDouble(r, "OrbitalPeriod_days"),
    Epoch_JD:            GetNullableDouble(r, "Epoch_JD"),
    T_Perihelion_JD:     GetNullableDouble(r, "T_Perihelion_JD"),
    GM_km3s2:            GetNullableDouble(r, "GM_km3s2"),
    MeanRadius_km:       GetNullableDouble(r, "MeanRadius_km"),
    EquatorialRadius_km: GetNullableDouble(r, "EquatorialRadius_km"),
    Mass_1e23kg:         GetNullableDouble(r, "Mass_1e23kg")
  );

  private static int    GetInt32(SqlDataReader r, string col)  => r.GetInt32(r.GetOrdinal(col));
  private static string GetString(SqlDataReader r, string col) => r.GetString(r.GetOrdinal(col));
  private static double GetDouble(SqlDataReader r, string col) => r.GetDouble(r.GetOrdinal(col));

  private static int? GetNullableInt32(SqlDataReader r, string col) {
    var ord = r.GetOrdinal(col);
    return r.IsDBNull(ord) ? null : r.GetInt32(ord);
  }
  private static double? GetNullableDouble(SqlDataReader r, string col) {
    var ord = r.GetOrdinal(col);
    return r.IsDBNull(ord) ? null : r.GetDouble(ord);
  }
  private static string? GetNullableString(SqlDataReader r, string col) {
    var ord = r.GetOrdinal(col);
    return r.IsDBNull(ord) ? null : r.GetString(ord);
  }
}
