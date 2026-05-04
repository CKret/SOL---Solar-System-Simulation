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
    b.Slug,
    b.DisplayName    AS Name,
    b.Kind,
    b.ParentBodyId,
    b.SortOrder,
    b.JplHorizonsId,
    b.SbdbDesig,
    b.H_AbsMag,
    b.HasEphemeris,
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

  public async Task<IReadOnlyList<BodySummary>> GetBodiesAsync(double? hMax, CancellationToken cancellationToken)
  {
    var sql = new StringBuilder("SELECT").Append(BodyColumns)
      .Append(" FROM dbo.Bodies b WHERE b.IsActive = 1");

    if (hMax.HasValue)
      sql.Append(" AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMax)");

    sql.Append(" ORDER BY b.SortOrder, b.DisplayName;");

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql.ToString(), connection);
    if (hMax.HasValue) command.Parameters.AddWithValue("@hMax", hMax.Value);
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
    HasEphemeris:        GetInt32(r, "HasEphemeris") != 0,
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
