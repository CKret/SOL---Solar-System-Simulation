using Microsoft.Data.SqlClient;
using Sol.Api.Models;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;

namespace Sol.Api.Services;

public sealed partial class SqlBodyCatalogImporter(
  IAuthoritativeBodyCatalogReader catalogReader,
  ISqlWriteConnectionFactory connectionFactory) : IBodyCatalogImporter
{
  private readonly IAuthoritativeBodyCatalogReader _catalogReader = catalogReader;
  private readonly ISqlWriteConnectionFactory _connectionFactory = connectionFactory;

  public async Task<BodyCatalogImportResult> ImportAsync(CancellationToken cancellationToken)
  {
    var seeds = await _catalogReader.ReadBodiesAsync(cancellationToken);
    var inserted = 0;
    var updated = 0;

    await using var connection = _connectionFactory.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

    var idsBySlug = await LoadExistingIdsAsync(connection, transaction, cancellationToken);
    List<CatalogBodySeed> pending = [..seeds];

    while (pending.Count > 0) {
      var progress = false;

      for (var index = pending.Count - 1; index >= 0; index--) {
        var seed = pending[index];
        if (seed.ParentSlug is not null && !idsBySlug.ContainsKey(seed.ParentSlug))
          continue;

        var parentId = seed.ParentSlug is null ? (int?)null : idsBySlug[seed.ParentSlug];
        var hadExisting = idsBySlug.TryGetValue(seed.Slug, out var existingId);
        var bodyId = await UpsertSeedAsync(connection, transaction, seed, parentId, hadExisting ? existingId : null, cancellationToken);
        if (hadExisting) updated++;
        else inserted++;

        idsBySlug[seed.Slug] = bodyId;
        pending.RemoveAt(index);
        progress = true;
      }

      if (!progress)
        throw new InvalidOperationException("Could not resolve parent relationships while importing body catalog.");
    }

    await DeactivateMissingBodiesAsync(connection, transaction, seeds.Select(s => s.Slug).ToArray(), cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return new BodyCatalogImportResult(inserted, updated, seeds.Count);
  }

  private static async Task<Dictionary<string, int>> LoadExistingIdsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
  {
    const string sql = "SELECT BodyId, Slug FROM dbo.Bodies;";
    await using var command = new SqlCommand(sql, connection, transaction);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    while (await reader.ReadAsync(cancellationToken))
      ids[reader.GetString(1)] = reader.GetInt32(0);
    return ids;
  }

  private static async Task<int> UpsertSeedAsync(SqlConnection connection, SqlTransaction transaction, CatalogBodySeed seed, int? parentId, int? existingId, CancellationToken cancellationToken)
  {
    var physicsJson = BuildPhysicsJson(seed);
    var minJd = seed.EphemerisMinJD;
    var maxJd = seed.EphemerisMaxJD;
    var hasEphemeris = minJd.HasValue;

    if (existingId is int bodyId) {
      const string sql = @"
UPDATE dbo.Bodies SET
  DisplayName          = @displayName,
  Kind                 = @kind,
  ParentBodyId         = @parentBodyId,
  SortOrder            = @sortOrder,
  IsActive             = 1,
  Source               = @source,
  JplHorizonsId        = @jplHorizonsId,
  SbdbDesig            = @sbdbDesig,
  H_AbsMag             = @hAbsMag,
  G_Slope              = @gSlope,
  HasEphemeris         = @hasEphemeris,
  EphemerisMinJD       = @ephMinJd,
  EphemerisMaxJD       = @ephMaxJd,
  EphemerisMinStr      = @ephMinStr,
  EphemerisMaxStr      = @ephMaxStr,
  Eccentricity         = @eccentricity,
  Perihelion_AU        = @perihelion,
  Aphelion_AU          = @aphelion,
  Inclination_deg      = @inclination,
  LongAscNode_deg      = @longAsc,
  ArgPerihelion_deg    = @argPeri,
  SemiMajorAxis_AU     = @semiMajor,
  MeanAnomaly_deg      = @meanAnom,
  MeanMotion_degPerDay = @meanMotion,
  OrbitalPeriod_days   = @period,
  Epoch_JD             = @epochJd,
  T_Perihelion_JD      = @tPeriJd,
  GM_km3s2             = @gm,
  MeanRadius_km        = @meanRadius,
  EquatorialRadius_km  = @eqRadius,
  Mass_1e23kg          = @mass,
  PhysicsJson          = @physicsJson,
  UpdatedUtc           = SYSUTCDATETIME()
WHERE BodyId = @bodyId;";
      await using var cmd = new SqlCommand(sql, connection, transaction);
      BindAll(cmd, seed, parentId, physicsJson, minJd, maxJd, hasEphemeris);
      cmd.Parameters.AddWithValue("@bodyId", bodyId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
      return bodyId;
    }

    const string insertSql = @"
INSERT INTO dbo.Bodies (
  Slug, DisplayName, Kind, ParentBodyId, SortOrder, IsActive,
  Source, JplHorizonsId, SbdbDesig, H_AbsMag, G_Slope,
  HasEphemeris, EphemerisMinJD, EphemerisMaxJD, EphemerisMinStr, EphemerisMaxStr,
  Eccentricity, Perihelion_AU, Aphelion_AU, Inclination_deg, LongAscNode_deg,
  ArgPerihelion_deg, SemiMajorAxis_AU, MeanAnomaly_deg, MeanMotion_degPerDay,
  OrbitalPeriod_days, Epoch_JD, T_Perihelion_JD,
  GM_km3s2, MeanRadius_km, EquatorialRadius_km, Mass_1e23kg, PhysicsJson
)
OUTPUT INSERTED.BodyId
VALUES (
  @slug, @displayName, @kind, @parentBodyId, @sortOrder, 1,
  @source, @jplHorizonsId, @sbdbDesig, @hAbsMag, @gSlope,
  @hasEphemeris, @ephMinJd, @ephMaxJd, @ephMinStr, @ephMaxStr,
  @eccentricity, @perihelion, @aphelion, @inclination, @longAsc,
  @argPeri, @semiMajor, @meanAnom, @meanMotion,
  @period, @epochJd, @tPeriJd,
  @gm, @meanRadius, @eqRadius, @mass, @physicsJson
);";
    await using var ins = new SqlCommand(insertSql, connection, transaction);
    BindAll(ins, seed, parentId, physicsJson, minJd, maxJd, hasEphemeris);
    var result = await ins.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result);
  }

  private static void BindAll(SqlCommand cmd, CatalogBodySeed seed, int? parentId, string? physicsJson, double? minJd, double? maxJd, bool hasEphemeris)
  {
    cmd.Parameters.AddWithValue("@slug",         seed.Slug);
    cmd.Parameters.AddWithValue("@displayName",  seed.DisplayName);
    cmd.Parameters.AddWithValue("@kind",         seed.Kind);
    cmd.Parameters.AddWithValue("@parentBodyId", parentId is null ? DBNull.Value : parentId.Value);
    cmd.Parameters.AddWithValue("@sortOrder",    seed.SortOrder);
    cmd.Parameters.AddWithValue("@source",       (object?)seed.Source       ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@jplHorizonsId",(object?)NormalizeJplHorizonsId(seed.JplId) ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@sbdbDesig",    (object?)seed.SbdbDesignation ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@hAbsMag",      (object?)seed.H_AbsMag     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@gSlope",       (object?)seed.G_Slope      ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@hasEphemeris", hasEphemeris ? 1 : 0);
    cmd.Parameters.AddWithValue("@ephMinJd",     (object?)minJd             ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ephMaxJd",     (object?)maxJd             ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ephMinStr",    (object?)seed.MinEpoch     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ephMaxStr",    (object?)seed.MaxEpoch     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@eccentricity", (object?)seed.Eccentricity ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@perihelion",   (object?)seed.Perihelion_AU ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@aphelion",     (object?)seed.Aphelion_AU  ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@inclination",  (object?)seed.Inclination_deg ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@longAsc",      (object?)seed.LongitudeOfAscendingNode_deg ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@argPeri",      (object?)seed.ArgumentOfPerihelion_deg     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@semiMajor",    (object?)seed.SemiMajorAxis_AU  ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@meanAnom",     (object?)seed.MeanAnomaly_deg   ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@meanMotion",   (object?)seed.MeanMotion_degPerDay ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@period",       (object?)seed.OrbitalPeriod_days ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@epochJd",      (object?)seed.Epoch_JD          ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@tPeriJd",      DBNull.Value);  // populated by future comet importer
    cmd.Parameters.AddWithValue("@gm",           (object?)seed.GM_km3s2          ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@meanRadius",   (object?)seed.MeanRadius_km     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@eqRadius",     (object?)seed.EquatorialRadius_km ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@mass",         (object?)seed.Mass_1e23kg        ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@physicsJson",  (object?)physicsJson             ?? DBNull.Value);
  }

  private static async Task DeactivateMissingBodiesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> activeSlugs, CancellationToken cancellationToken)
  {
    // Only manage bodies owned by this importer (horizons/sbdb/null source).
    // External importers like mpcorb manage their own IsActive state.
    const string sql = @"
UPDATE dbo.Bodies
SET IsActive   = CASE WHEN Slug IN (SELECT value FROM string_split(@slugs, ',')) THEN 1 ELSE 0 END,
    UpdatedUtc = SYSUTCDATETIME()
WHERE Source IN ('horizons', 'sbdb') OR Source IS NULL;";
    await using var cmd = new SqlCommand(sql, connection, transaction);
    cmd.Parameters.AddWithValue("@slugs", string.Join(',', activeSlugs));
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  // Serialise the physical properties that were individual columns into a JSON blob.
  private static string? BuildPhysicsJson(CatalogBodySeed s)
  {
    var d = new Dictionary<string, double>();
    void Add(string k, double? v) { if (v.HasValue) d[k] = v.Value; }
    Add("density_gcm3",          s.Density_gcm3);
    Add("volume_1e10km3",        s.Volume_1e10km3);
    Add("sidRotPeriod_d",        s.SiderealRotPeriod_d);
    Add("sidRotRate_radps",      s.SiderealRotRate_radps);
    Add("meanSolarDay_d",        s.MeanSolarDay_d);
    Add("coreRadius_km",         s.CoreRadius_km);
    Add("geometricAlbedo",       s.GeometricAlbedo);
    Add("surfaceEmissivity",     s.SurfaceEmissivity);
    Add("massRatioSunPlanet",    s.MassRatioSunPlanet);
    Add("momentOfInertia",       s.MomentOfInertia);
    Add("eqGravity_ms2",         s.EquatorialGravity_ms2);
    Add("atmosPressure_bar",     s.AtmosPressure_bar);
    Add("maxAngDiam_arcsec",     s.MaxAngularDiam_arcsec);
    Add("meanTemp_K",            s.MeanTemperature_K);
    Add("visualMag",             s.VisualMag);
    Add("obliquity_arcmin",      s.ObliquityToOrbit_arcmin);
    Add("hillSphere_Rp",         s.HillSphereRadius_Rp);
    Add("sidOrbPeriod_y",        s.SiderealOrbPeriod_y);
    Add("sidOrbPeriod_d",        s.SiderealOrbPeriod_d);
    Add("escapeVel_kms",         s.EscapeVelocity_kms);
    Add("meanOrbitVel_kms",      s.MeanOrbitVelocity_kms);
    Add("solarConst_mean_Wm2",   s.SolarConstant_Wm2_Mean);
    Add("solarConst_peri_Wm2",   s.SolarConstant_Wm2_Perihelion);
    Add("solarConst_aph_Wm2",    s.SolarConstant_Wm2_Aphelion);
    Add("maxIR_mean_Wm2",        s.MaxPlanetaryIR_Wm2_Mean);
    Add("maxIR_peri_Wm2",        s.MaxPlanetaryIR_Wm2_Perihelion);
    Add("maxIR_aph_Wm2",         s.MaxPlanetaryIR_Wm2_Aphelion);
    Add("minIR_mean_Wm2",        s.MinPlanetaryIR_Wm2_Mean);
    Add("minIR_peri_Wm2",        s.MinPlanetaryIR_Wm2_Perihelion);
    Add("minIR_aph_Wm2",         s.MinPlanetaryIR_Wm2_Aphelion);
    return d.Count == 0 ? null : JsonSerializer.Serialize(d);
  }

  private static string? NormalizeJplHorizonsId(string? raw) =>
    string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

  // Convert a JPL epoch string ("BC 9999-Jan-01 12:00" or "2500-Jan-01 12:00") to a Julian Day number.
  // BC dates cannot be stored in DATETIME2, so JD (FLOAT) is used for programmatic comparisons.
  private static double? ParseEpochToJD(string? epochStr)
  {
    if (string.IsNullOrWhiteSpace(epochStr)) return null;

    var s = epochStr.Trim();
    if (s.StartsWith("AD ", StringComparison.OrdinalIgnoreCase)) s = s[3..].TrimStart();
    bool isBc = s.StartsWith("BC ", StringComparison.OrdinalIgnoreCase);
    if (isBc) s = s[3..].TrimStart();

    var m = EpochDateRegex().Match(s);
    if (!m.Success) return null;

    var year  = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    if (isBc) year = 1 - year;   // BC 1 → 0, BC 2 → -1, BC n → 1-n (astronomical year)
    var month = MonthAbbrevToInt(m.Groups[2].Value);
    var day   = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    var hour  = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 12;
    var min   = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
    var ut    = hour + min / 60.0;

    return GregorianToJD(year, month, day, ut);
  }

  private static int MonthAbbrevToInt(string abbr) => abbr.ToLowerInvariant() switch {
    "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
    "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
    _ => 1
  };

  // Julian Day from proleptic Gregorian calendar (year is astronomical, i.e. 1 BC = year 0).
  private static double GregorianToJD(int year, int month, int day, double ut = 12.0)
  {
    var a = (14 - month) / 12;
    var y = year + 4800 - a;
    var m = month + 12 * a - 3;
    var jdn = day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    return jdn - 0.5 + ut / 24.0;
  }

  [GeneratedRegex(@"^(\d{1,4})-(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)-(\d{1,2})(?:\s+(\d{1,2}):(\d{2}))?", RegexOptions.IgnoreCase)]
  private static partial Regex EpochDateRegex();
}
