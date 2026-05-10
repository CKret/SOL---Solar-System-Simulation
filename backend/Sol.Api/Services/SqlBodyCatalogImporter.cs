using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sol.Api.Data;
using Sol.Api.Data.Entities;
using Sol.Api.Models;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;

namespace Sol.Api.Services;

public sealed partial class SqlBodyCatalogImporter(
    IAuthoritativeBodyCatalogReader catalogReader,
    IDbContextFactory<SolWriteDbContext> dbContextFactory) : IBodyCatalogImporter
{
    public async Task<BodyCatalogImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        var seeds = await catalogReader.ReadBodiesAsync(cancellationToken);
        int inserted = 0, updated = 0;

        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx  = await ctx.Database.BeginTransactionAsync(cancellationToken);

        var idsBySlug = await ctx.Bodies
            .Select(b => new { b.BodyId, b.Slug })
            .ToDictionaryAsync(x => x.Slug, x => x.BodyId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        List<CatalogBodySeed> pending = [..seeds];

        while (pending.Count > 0)
        {
            var progress = false;

            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var seed = pending[index];
                if (seed.ParentSlug is not null && !idsBySlug.ContainsKey(seed.ParentSlug))
                    continue;

                var parentId    = seed.ParentSlug is null ? (int?)null : idsBySlug[seed.ParentSlug];
                var hadExisting = idsBySlug.TryGetValue(seed.Slug, out var existingId);
                var bodyId      = await UpsertSeedAsync(ctx, seed, parentId, hadExisting ? existingId : null, cancellationToken);
                if (hadExisting) updated++;
                else inserted++;

                idsBySlug[seed.Slug] = bodyId;
                pending.RemoveAt(index);
                progress = true;
            }

            if (!progress)
                throw new InvalidOperationException("Could not resolve parent relationships while importing body catalog.");
        }

        await DeactivateMissingBodiesAsync(ctx, seeds.Select(s => s.Slug).ToArray(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new BodyCatalogImportResult(inserted, updated, seeds.Count);
    }

    private static async Task<int> UpsertSeedAsync(
        SolWriteDbContext ctx,
        CatalogBodySeed seed,
        int? parentId,
        int? existingId,
        CancellationToken cancellationToken)
    {
        var physicsJson = BuildPhysicsJson(seed);
        var minJd       = seed.EphemerisMinJD;
        var maxJd       = seed.EphemerisMaxJD;
        var hasEphemeris = minJd.HasValue;

        if (existingId is int bodyId)
        {
            // Attach a stub entity, then set only the columns the catalog sync owns.
            // EF Core tracks each modified property individually — CreatedUtc and
            // Slug are intentionally left untouched so they stay Unchanged.
            var entity = new BodyEntity { BodyId = bodyId };
            ctx.Bodies.Attach(entity);
            entity.DisplayName          = seed.DisplayName;
            entity.Kind                 = seed.Kind;
            entity.ParentBodyId         = parentId;
            entity.SortOrder            = seed.SortOrder;
            entity.IsActive             = true;
            entity.Source               = seed.Source;
            entity.JplHorizonsId        = NormalizeJplHorizonsId(seed.JplId);
            entity.SbdbDesig            = seed.SbdbDesignation;
            entity.H_AbsMag             = seed.H_AbsMag;
            entity.G_Slope              = seed.G_Slope;
            entity.HasEphemeris         = hasEphemeris;
            entity.EphemerisMinJD       = minJd;
            entity.EphemerisMaxJD       = maxJd;
            entity.EphemerisMinStr      = seed.MinEpoch;
            entity.EphemerisMaxStr      = seed.MaxEpoch;
            entity.Eccentricity         = seed.Eccentricity;
            entity.Perihelion_AU        = seed.Perihelion_AU;
            entity.Aphelion_AU          = seed.Aphelion_AU;
            entity.Inclination_deg      = seed.Inclination_deg;
            entity.LongAscNode_deg      = seed.LongitudeOfAscendingNode_deg;
            entity.ArgPerihelion_deg    = seed.ArgumentOfPerihelion_deg;
            entity.SemiMajorAxis_AU     = seed.SemiMajorAxis_AU;
            entity.MeanAnomaly_deg      = seed.MeanAnomaly_deg;
            entity.MeanMotion_degPerDay = seed.MeanMotion_degPerDay;
            entity.OrbitalPeriod_days   = seed.OrbitalPeriod_days;
            entity.Epoch_JD             = seed.Epoch_JD;
            entity.T_Perihelion_JD      = null; // populated by future comet importer
            entity.GM_km3s2             = seed.GM_km3s2;
            entity.MeanRadius_km        = seed.MeanRadius_km;
            entity.EquatorialRadius_km  = seed.EquatorialRadius_km;
            entity.Mass_1e23kg          = seed.Mass_1e23kg;
            entity.PhysicsJson          = physicsJson;
            entity.UpdatedUtc           = DateTime.UtcNow;
            await ctx.SaveChangesAsync(cancellationToken);
            return bodyId;
        }

        var newEntity = new BodyEntity
        {
            Slug                 = seed.Slug,
            DisplayName          = seed.DisplayName,
            Kind                 = seed.Kind,
            ParentBodyId         = parentId,
            SortOrder            = seed.SortOrder,
            IsActive             = true,
            Source               = seed.Source,
            JplHorizonsId        = NormalizeJplHorizonsId(seed.JplId),
            SbdbDesig            = seed.SbdbDesignation,
            H_AbsMag             = seed.H_AbsMag,
            G_Slope              = seed.G_Slope,
            HasEphemeris         = hasEphemeris,
            EphemerisMinJD       = minJd,
            EphemerisMaxJD       = maxJd,
            EphemerisMinStr      = seed.MinEpoch,
            EphemerisMaxStr      = seed.MaxEpoch,
            Eccentricity         = seed.Eccentricity,
            Perihelion_AU        = seed.Perihelion_AU,
            Aphelion_AU          = seed.Aphelion_AU,
            Inclination_deg      = seed.Inclination_deg,
            LongAscNode_deg      = seed.LongitudeOfAscendingNode_deg,
            ArgPerihelion_deg    = seed.ArgumentOfPerihelion_deg,
            SemiMajorAxis_AU     = seed.SemiMajorAxis_AU,
            MeanAnomaly_deg      = seed.MeanAnomaly_deg,
            MeanMotion_degPerDay = seed.MeanMotion_degPerDay,
            OrbitalPeriod_days   = seed.OrbitalPeriod_days,
            Epoch_JD             = seed.Epoch_JD,
            T_Perihelion_JD      = null,
            GM_km3s2             = seed.GM_km3s2,
            MeanRadius_km        = seed.MeanRadius_km,
            EquatorialRadius_km  = seed.EquatorialRadius_km,
            Mass_1e23kg          = seed.Mass_1e23kg,
            PhysicsJson          = physicsJson,
            CreatedUtc           = DateTime.UtcNow,
            UpdatedUtc           = DateTime.UtcNow,
        };
        ctx.Bodies.Add(newEntity);
        await ctx.SaveChangesAsync(cancellationToken);
        return newEntity.BodyId;
    }

    private static async Task DeactivateMissingBodiesAsync(
        SolWriteDbContext ctx,
        IReadOnlyList<string> activeSlugs,
        CancellationToken cancellationToken)
    {
        // NOT IN with hundreds of parameters forces a full table scan on a table that may contain
        // millions of MPCORB rows. Instead, load active slugs into a temp table with a clustered
        // primary key so SQL Server can use NOT EXISTS with index seeks.
        var insertValues = string.Join(", ", activeSlugs.Select((_, i) => $"(@s{i})"));
        var sqlParams    = activeSlugs.Select((s, i) => new SqlParameter($"@s{i}", s)).Cast<object>().ToList();

#pragma warning disable EF1002 // parameter placeholders are code-generated, not user input
        var sql = $@"
            CREATE TABLE #active_slugs (Slug NVARCHAR(64) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY CLUSTERED);
            INSERT INTO #active_slugs VALUES {insertValues};
            UPDATE b SET b.IsActive = 0, b.UpdatedUtc = SYSUTCDATETIME()
            FROM dbo.Bodies b
            WHERE (b.Source IN ('horizons', 'sbdb') OR b.Source IS NULL)
              AND b.IsActive = 1
              AND NOT EXISTS (SELECT 1 FROM #active_slugs a WHERE a.Slug = b.Slug);
            UPDATE b SET b.IsActive = 1, b.UpdatedUtc = SYSUTCDATETIME()
            FROM dbo.Bodies b
            WHERE (b.Source IN ('horizons', 'sbdb') OR b.Source IS NULL)
              AND b.IsActive = 0
              AND EXISTS (SELECT 1 FROM #active_slugs a WHERE a.Slug = b.Slug);
            DROP TABLE #active_slugs;";

        ctx.Database.SetCommandTimeout(300);
        await ctx.Database.ExecuteSqlRawAsync(sql, sqlParams, cancellationToken);
        ctx.Database.SetCommandTimeout(null);
#pragma warning restore EF1002
    }

    // ── Static helpers ────────────────────────────────────────────────────────

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
        if (isBc) year = 1 - year;
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
