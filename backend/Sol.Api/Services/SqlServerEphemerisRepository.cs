using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sol.Api.Data;
using Sol.Api.Data.Entities;
using Sol.Api.Models;

namespace Sol.Api.Services;

public sealed class SqlServerEphemerisRepository(IDbContextFactory<SolReadDbContext> factory) : IEphemerisRepository
{
    // ── Body queries ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BodySummary>> GetBodiesAsync(double? hMax, int? maxBodies, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);

        var query = ctx.Bodies.AsNoTracking().Where(b => b.IsActive);

        if (hMax.HasValue)
            query = query.Where(b => b.H_AbsMag == null || b.H_AbsMag <= hMax.Value);
        else
        {
            var kinds = new[] { "star", "planet", "probe", "moon", "dwarf-planet", "comet" };
            query = query.Where(b => b.Source != "mpcorb" && kinds.Contains(b.Kind));
        }

        query = query.OrderBy(b => b.SortOrder).ThenBy(b => b.DisplayName);

        if (maxBodies.HasValue)
            query = query.Take(Math.Max(1, maxBodies.Value));

        var entities = await query.ToListAsync(cancellationToken);
        return entities.ConvertAll(ToSummary);
    }

    public async Task<IReadOnlyList<BodySummary>> GetBodiesBatchAsync(
        double? hMinExclusive, double? hMaxInclusive, int take, int? afterBodyId, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);

        var query = ctx.Bodies.AsNoTracking().Where(b => b.IsActive);

        if (afterBodyId.HasValue)
            query = query.Where(b => b.BodyId > afterBodyId.Value);

        if (hMaxInclusive.HasValue)
        {
            if (hMinExclusive.HasValue)
                query = query.Where(b => b.H_AbsMag != null && b.H_AbsMag > hMinExclusive.Value && b.H_AbsMag <= hMaxInclusive.Value);
            else
                query = query.Where(b => b.H_AbsMag == null || b.H_AbsMag <= hMaxInclusive.Value);
        }
        else
        {
            query = query.Where(b => b.Source != "mpcorb");
        }

        var entities = await query
            .OrderBy(b => b.BodyId)
            .Take(Math.Clamp(take, 1, 50_000))
            .ToListAsync(cancellationToken);

        return entities.ConvertAll(ToSummary);
    }

    public async Task<IReadOnlyList<BodySummary>> SearchBodiesAsync(
        string? query,
        int limit,
        bool completedEphemerisOnly,
        bool namedOnly,
        CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);

        var q = ctx.Bodies.AsNoTracking().Where(b => b.IsActive);

        if (completedEphemerisOnly)
            q = q.Where(b => b.CompletedEphemeris);

        if (namedOnly)
            q = q.Where(b =>
                b.DisplayName.Trim().Length > 0 &&
                !EF.Functions.Like(b.DisplayName.Trim(), "[0-9]%"));

        var trimmed = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (trimmed is not null)
        {
            var contains = $"%{trimmed}%";
            var prefix   = $"{trimmed}%";
            q = q.Where(b =>
                EF.Functions.Like(b.DisplayName, contains) ||
                EF.Functions.Like(b.Slug,        contains) ||
                EF.Functions.Like(b.SbdbDesig,   contains) ||
                EF.Functions.Like(b.JplHorizonsId, contains));

            q = q.OrderBy(b => EF.Functions.Like(b.DisplayName, prefix) ? 0 : 1)
                 .ThenBy(b => EF.Functions.Like(b.Slug, prefix) ? 0 : 1)
                 .ThenBy(b => b.SortOrder)
                 .ThenBy(b => b.DisplayName);
        }
        else
        {
            q = q.OrderBy(b => b.SortOrder).ThenBy(b => b.DisplayName);
        }

        var entities = await q.Take(Math.Clamp(limit, 1, 2000)).ToListAsync(cancellationToken);
        return entities.ConvertAll(ToSummary);
    }

    public async Task<BodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await ctx.Bodies.AsNoTracking()
            .FirstOrDefaultAsync(b => b.IsActive && b.Slug == slug, cancellationToken);
        return entity is null ? null : ToSummary(entity);
    }

    // ── Ephemeris sample queries ──────────────────────────────────────────────

    public async Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(
        int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken)
    {
        double startJd = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
        double endJd   = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc,   DateTimeKind.Utc));

        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await ctx.EphemerisSamples.AsNoTracking()
            .Where(s => s.BodyId == bodyId && s.SampleJd >= startJd && s.SampleJd <= endJd)
            .OrderBy(s => s.SampleJd)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(s => new EphemerisSample(s.BodyId, s.SampleJd,
            s.X_AU, s.Y_AU, s.Z_AU,
            s.VX_AUPerDay, s.VY_AUPerDay, s.VZ_AUPerDay,
            s.Frame));
    }

    public async Task<IReadOnlyList<EphemerisSample>> GetBulkSamplesAsync(
        DateTime startUtc, DateTime endUtc, double? hMax, int step, int? maxBodies, CancellationToken cancellationToken)
    {
        double startJd = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
        double endJd   = JulianDateConverter.FromDateTime(DateTime.SpecifyKind(endUtc,   DateTimeKind.Utc));

        var hFilter = hMax.HasValue
            ? "AND (b.H_AbsMag IS NULL OR b.H_AbsMag <= @hMax)"
            : "AND b.Source != 'mpcorb' AND b.Kind IN ('star', 'planet', 'probe', 'moon', 'dwarf-planet', 'comet')";

        // Subquery-based SQL (no CTEs so EF Core can safely wrap it).
        var sql = step <= 1 ? $@"
SELECT e.BodyId, e.SampleJd,
  e.X_AU AS X, e.Y_AU AS Y, e.Z_AU AS Z,
  e.VX_AUPerDay AS Vx, e.VY_AUPerDay AS Vy, e.VZ_AUPerDay AS Vz
FROM dbo.EphemerisSamples e
INNER JOIN (
  SELECT TOP (@maxBodies) b.BodyId
  FROM dbo.Bodies b
  WHERE b.IsActive = 1 AND b.HasEphemeris = 1
    {hFilter}
  ORDER BY
    CASE WHEN b.Source = 'mpcorb' THEN 1 ELSE 0 END,
    COALESCE(b.SortOrder, 2147483647),
    CASE WHEN b.H_AbsMag IS NULL THEN 0 ELSE 1 END,
    ISNULL(b.H_AbsMag, 0),
    b.BodyId
) AS sb ON sb.BodyId = e.BodyId
WHERE e.SampleJd >= @startJd AND e.SampleJd <= @endJd"
: $@"
SELECT BodyId, SampleJd, X, Y, Z, Vx, Vy, Vz
FROM (
  SELECT e.BodyId, e.SampleJd,
    e.X_AU AS X, e.Y_AU AS Y, e.Z_AU AS Z,
    e.VX_AUPerDay AS Vx, e.VY_AUPerDay AS Vy, e.VZ_AUPerDay AS Vz,
    ROW_NUMBER() OVER (PARTITION BY e.BodyId ORDER BY e.SampleJd) AS rn
  FROM dbo.EphemerisSamples e
  INNER JOIN (
    SELECT TOP (@maxBodies) b.BodyId
    FROM dbo.Bodies b
    WHERE b.IsActive = 1 AND b.HasEphemeris = 1
      {hFilter}
    ORDER BY
      CASE WHEN b.Source = 'mpcorb' THEN 1 ELSE 0 END,
      COALESCE(b.SortOrder, 2147483647),
      CASE WHEN b.H_AbsMag IS NULL THEN 0 ELSE 1 END,
      ISNULL(b.H_AbsMag, 0),
      b.BodyId
  ) AS sb ON sb.BodyId = e.BodyId
  WHERE e.SampleJd >= @startJd AND e.SampleJd <= @endJd
) AS ranked
WHERE rn % @step = 1";

        var sqlParams = new List<object>
        {
            new SqlParameter("@startJd",   startJd),
            new SqlParameter("@endJd",     endJd),
            new SqlParameter("@maxBodies", Math.Max(1, maxBodies ?? int.MaxValue)),
        };
        if (hMax.HasValue) sqlParams.Add(new SqlParameter("@hMax",  hMax.Value));
        if (step > 1)      sqlParams.Add(new SqlParameter("@step",  step));

        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await ctx.Database.SqlQueryRaw<BulkSampleRow>(sql, sqlParams)
            .OrderBy(r => r.SampleJd).ThenBy(r => r.BodyId)
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(r => new EphemerisSample(r.BodyId, r.SampleJd,
            r.X, r.Y, r.Z, r.Vx, r.Vy, r.Vz, Frame: null));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BodySummary ToSummary(BodyEntity b) => new(
        Id:                   b.BodyId,
        Slug:                 string.IsNullOrWhiteSpace(b.Slug)        ? $"body-{b.BodyId}" : b.Slug.Trim(),
        Name:                 !string.IsNullOrWhiteSpace(b.DisplayName) ? b.DisplayName.Trim()
                            : !string.IsNullOrWhiteSpace(b.Slug)        ? b.Slug.Trim()
                            : $"body-{b.BodyId}",
        Kind:                 string.IsNullOrWhiteSpace(b.Kind)        ? "small-body" : b.Kind.Trim(),
        ParentBodyId:         b.ParentBodyId,
        SortOrder:            b.SortOrder,
        JplHorizonsId:        b.JplHorizonsId,
        SbdbDesig:            b.SbdbDesig,
        H_AbsMag:             b.H_AbsMag,
        HasEphemeris:         b.HasEphemeris,
        EphemerisMinJD:       b.EphemerisMinJD,
        EphemerisMaxJD:       b.EphemerisMaxJD,
        EphemerisMinStr:      b.EphemerisMinStr,
        EphemerisMaxStr:      b.EphemerisMaxStr,
        Eccentricity:         b.Eccentricity,
        Perihelion_AU:        b.Perihelion_AU,
        Aphelion_AU:          b.Aphelion_AU,
        Inclination_deg:      b.Inclination_deg,
        LongAscNode_deg:      b.LongAscNode_deg,
        ArgPerihelion_deg:    b.ArgPerihelion_deg,
        SemiMajorAxis_AU:     b.SemiMajorAxis_AU,
        MeanAnomaly_deg:      b.MeanAnomaly_deg,
        MeanMotion_degPerDay: b.MeanMotion_degPerDay,
        OrbitalPeriod_days:   b.OrbitalPeriod_days,
        Epoch_JD:             b.Epoch_JD,
        T_Perihelion_JD:      b.T_Perihelion_JD,
        GM_km3s2:             b.GM_km3s2,
        MeanRadius_km:        b.MeanRadius_km,
        EquatorialRadius_km:  b.EquatorialRadius_km,
        Mass_1e23kg:          b.Mass_1e23kg
    );

    private sealed record BulkSampleRow(
        int BodyId, double SampleJd,
        double X, double Y, double Z,
        double? Vx, double? Vy, double? Vz);
}
