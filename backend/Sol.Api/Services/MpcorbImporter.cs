using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sol.Api.Services;

public sealed class MpcorbImporter(HttpClient httpClient, ISqlWriteConnectionFactory connectionFactory)
{
    private const string NeaUrl    = "https://minorplanetcenter.net/Extended_Files/nea_extended.json.gz";
    private const string MpcorbUrl = "https://minorplanetcenter.net/Extended_Files/mpcorb_extended.json.gz";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<(int Inserted, int Updated, int Total)> ImportAsync(bool fullCatalog, CancellationToken cancellationToken)
    {
        var url = fullCatalog ? MpcorbUrl : NeaUrl;
        Console.WriteLine($"Downloading {url}...");

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

        var table = CreateStagingTable();
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        await foreach (var rec in JsonSerializer.DeserializeAsyncEnumerable<MpcorbJsonRecord>(gzip, JsonOpts, cancellationToken))
        {
            if (rec is null || rec.A <= 0) continue;
            if (!TryBuildRow(rec, fullCatalog, out var row)) continue;
            if (!seenSlugs.Add(row.Slug)) continue; // skip duplicate slugs
            AddRowToTable(table, row);
            total++;
            if (total % 5000 == 0)
                Console.WriteLine($"  Parsed {total:N0}...");
        }

        Console.WriteLine($"Parsed {total:N0} records. Merging into DB...");
        var (inserted, updated) = await BulkUpsertAsync(table, cancellationToken);
        return (inserted, updated, total);
    }

    private static bool TryBuildRow(MpcorbJsonRecord rec, bool fullCatalog, out MpcorbSeedRow row)
    {
        row = default;
        var num  = ParseNumber(rec.Number);
        var slug = MakeSlug(num, rec.Principal_desig);
        if (string.IsNullOrEmpty(slug)) return false;

        var isPha = rec.PHA_flag == 1;
        var kind  = ClassifyKind(rec.Orbit_type, isPha);

        // Prefer pre-computed distances; fall back to Keplerian derivation.
        var perihelion = rec.Perihelion_dist ?? (rec.A > 0 ? rec.A * (1.0 - rec.E) : (double?)null);
        var aphelion   = rec.Aphelion_dist   ?? (rec.A > 0 ? rec.A * (1.0 + rec.E) : (double?)null);
        var periodDays = rec.Orbital_period.HasValue ? rec.Orbital_period.Value * 365.25
                        : rec.A > 0 ? Math.Pow(rec.A, 1.5) * 365.25 : (double?)null;

        // Sort brightest first (lowest H = brightest); unknown H goes last.
        var sortOrder = rec.H.HasValue ? (int)(rec.H.Value * 100) + 1_000_000 : 9_999_999;

        row = new MpcorbSeedRow(
            Slug:                slug,
            DisplayName:         MakeDisplayName(num, rec.Name, rec.Principal_desig),
            Kind:                kind,
            H:                   rec.H,
            G:                   rec.G,
            Eccentricity:        rec.E,
            Perihelion_AU:       perihelion,
            Aphelion_AU:         aphelion,
            Inclination_deg:     rec.I,
            LongAscNode_deg:     rec.Node,
            ArgPerihelion_deg:   rec.Peri,
            SemiMajorAxis_AU:    rec.A > 0 ? rec.A : (double?)null,
            MeanAnomaly_deg:     rec.M,
            MeanMotion_degPerDay:rec.N,
            OrbitalPeriod_days:  periodDays,
            Epoch_JD:            rec.Epoch,
            T_Perihelion_JD:     rec.Tp,
            JplHorizonsId:       MakeJplId(num, rec.Principal_desig),
            SbdbDesig:           rec.Principal_desig,
            SortOrder:           sortOrder
        );
        return true;
    }

    // MPC Number field comes as "(433)", "(1)", or null for unnumbered.
    private static int? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim('(', ')').Trim();
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string ClassifyKind(string? orbitType, bool isPha)
    {
        if (isPha) return "pha";
        return orbitType?.ToLowerInvariant() switch
        {
            "aten"   => "nea-aten",
            "apollo" => "nea-apollo",
            "amor"   => "nea-amor",
            "atira"  => "nea-atira",
            _        => "nea"
        };
    }

    private static string MakeSlug(int? number, string? desig)
    {
        if (number.HasValue) return $"mpc-{number.Value}";
        if (!string.IsNullOrWhiteSpace(desig)) return "mpc-" + Slugify(desig);
        return string.Empty;
    }

    private static string MakeDisplayName(int? number, string? name, string? desig)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return number.HasValue ? $"{name} ({number.Value})" : name;
        if (!string.IsNullOrWhiteSpace(desig))
            return number.HasValue ? $"{desig} ({number.Value})" : desig;
        return number?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
    }

    private static string? MakeJplId(int? number, string? desig)
    {
        if (number.HasValue)  return $"{number.Value};";
        if (!string.IsNullOrWhiteSpace(desig)) return $"DES={desig};";
        return null;
    }

    private static string Slugify(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value.Trim().ToLowerInvariant().Replace(' ', '-').Replace('/', '-'),
            @"[^a-z0-9\-]+", "-")
        .Trim('-');

    // ── Staging table ─────────────────────────────────────────────────────────

    private static DataTable CreateStagingTable()
    {
        var t = new DataTable();
        t.Columns.Add("Slug",                typeof(string));
        t.Columns.Add("DisplayName",         typeof(string));
        t.Columns.Add("Kind",                typeof(string));
        t.Columns.Add("H_AbsMag",            typeof(double));
        t.Columns.Add("G_Slope",             typeof(double));
        t.Columns.Add("Eccentricity",        typeof(double));
        t.Columns.Add("Perihelion_AU",       typeof(double));
        t.Columns.Add("Aphelion_AU",         typeof(double));
        t.Columns.Add("Inclination_deg",     typeof(double));
        t.Columns.Add("LongAscNode_deg",     typeof(double));
        t.Columns.Add("ArgPerihelion_deg",   typeof(double));
        t.Columns.Add("SemiMajorAxis_AU",    typeof(double));
        t.Columns.Add("MeanAnomaly_deg",     typeof(double));
        t.Columns.Add("MeanMotion_degPerDay",typeof(double));
        t.Columns.Add("OrbitalPeriod_days",  typeof(double));
        t.Columns.Add("Epoch_JD",            typeof(double));
        t.Columns.Add("T_Perihelion_JD",     typeof(double));
        t.Columns.Add("JplHorizonsId",       typeof(string));
        t.Columns.Add("SbdbDesig",           typeof(string));
        t.Columns.Add("SortOrder",           typeof(int));
        return t;
    }

    private static void AddRowToTable(DataTable t, MpcorbSeedRow r)
    {
        var row = t.NewRow();
        row["Slug"]                 = r.Slug;
        row["DisplayName"]          = r.DisplayName;
        row["Kind"]                 = r.Kind;
        row["H_AbsMag"]             = r.H.HasValue       ? r.H.Value            : DBNull.Value;
        row["G_Slope"]              = r.G.HasValue       ? r.G.Value            : DBNull.Value;
        row["Eccentricity"]         = r.Eccentricity;
        row["Perihelion_AU"]        = r.Perihelion_AU;
        row["Aphelion_AU"]          = r.Aphelion_AU;
        row["Inclination_deg"]      = r.Inclination_deg;
        row["LongAscNode_deg"]      = r.LongAscNode_deg;
        row["ArgPerihelion_deg"]    = r.ArgPerihelion_deg;
        row["SemiMajorAxis_AU"]     = r.SemiMajorAxis_AU;
        row["MeanAnomaly_deg"]      = r.MeanAnomaly_deg;
        row["MeanMotion_degPerDay"] = r.MeanMotion_degPerDay;
        row["OrbitalPeriod_days"]   = r.OrbitalPeriod_days;
        row["Epoch_JD"]             = r.Epoch_JD.HasValue        ? r.Epoch_JD.Value        : DBNull.Value;
        row["T_Perihelion_JD"]      = r.T_Perihelion_JD.HasValue ? r.T_Perihelion_JD.Value : DBNull.Value;
        row["JplHorizonsId"]        = r.JplHorizonsId is not null ? r.JplHorizonsId : DBNull.Value;
        row["SbdbDesig"]            = r.SbdbDesig    is not null ? r.SbdbDesig    : DBNull.Value;
        row["SortOrder"]            = r.SortOrder;
        t.Rows.Add(row);
    }

    // ── Bulk upsert via staging table + MERGE ─────────────────────────────────

    private async Task<(int Inserted, int Updated)> BulkUpsertAsync(DataTable table, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string createStage = @"
            CREATE TABLE #mpc_stage (
                Slug                 NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL,
                DisplayName          NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL,
                Kind                 NVARCHAR(32)  COLLATE DATABASE_DEFAULT NOT NULL,
                H_AbsMag             FLOAT NULL,
                G_Slope              FLOAT NULL,
                Eccentricity         FLOAT NULL,
                Perihelion_AU        FLOAT NULL,
                Aphelion_AU          FLOAT NULL,
                Inclination_deg      FLOAT NULL,
                LongAscNode_deg      FLOAT NULL,
                ArgPerihelion_deg    FLOAT NULL,
                SemiMajorAxis_AU     FLOAT NULL,
                MeanAnomaly_deg      FLOAT NULL,
                MeanMotion_degPerDay FLOAT NULL,
                OrbitalPeriod_days   FLOAT NULL,
                Epoch_JD             FLOAT NULL,
                T_Perihelion_JD      FLOAT NULL,
                JplHorizonsId        NVARCHAR(64)  COLLATE DATABASE_DEFAULT NULL,
                SbdbDesig            NVARCHAR(64)  COLLATE DATABASE_DEFAULT NULL,
                SortOrder            INT NOT NULL
            );";
        await using (var cmd = new SqlCommand(createStage, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#mpc_stage", BulkCopyTimeout = 0 })
        {
            foreach (DataColumn col in table.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(table, cancellationToken);
        }

        Console.WriteLine("Staging loaded. Running MERGE...");

        // Default epoch coverage for small bodies per JPL Horizons time_spans: 1599-Dec-10 23:59 to 2500-Dec-31 23:58.
        // COALESCE on update preserves any authoritative value already written by a JPL epoch query.
        const string merge = @"
            MERGE dbo.Bodies AS tgt
            USING #mpc_stage AS src ON tgt.Slug = src.Slug
            WHEN MATCHED THEN UPDATE SET
                DisplayName          = src.DisplayName,
                Kind                 = src.Kind,
                H_AbsMag             = src.H_AbsMag,
                G_Slope              = src.G_Slope,
                Eccentricity         = src.Eccentricity,
                Perihelion_AU        = src.Perihelion_AU,
                Aphelion_AU          = src.Aphelion_AU,
                Inclination_deg      = src.Inclination_deg,
                LongAscNode_deg      = src.LongAscNode_deg,
                ArgPerihelion_deg    = src.ArgPerihelion_deg,
                SemiMajorAxis_AU     = src.SemiMajorAxis_AU,
                MeanAnomaly_deg      = src.MeanAnomaly_deg,
                MeanMotion_degPerDay = src.MeanMotion_degPerDay,
                OrbitalPeriod_days   = src.OrbitalPeriod_days,
                Epoch_JD             = src.Epoch_JD,
                T_Perihelion_JD      = src.T_Perihelion_JD,
                JplHorizonsId        = src.JplHorizonsId,
                SbdbDesig            = src.SbdbDesig,
                SortOrder            = src.SortOrder,
                EphemerisMinJD       = COALESCE(tgt.EphemerisMinJD, 2305426.499),
                EphemerisMaxJD       = COALESCE(tgt.EphemerisMaxJD, 2634531.499),
                EphemerisMinStr      = COALESCE(tgt.EphemerisMinStr, '1599-Dec-10 23:59'),
                EphemerisMaxStr      = COALESCE(tgt.EphemerisMaxStr, '2500-Dec-31 23:58'),
                IsActive             = 1,
                UpdatedUtc           = SYSUTCDATETIME()
            WHEN NOT MATCHED BY TARGET THEN INSERT (
                Slug, DisplayName, Kind, IsActive, HasEphemeris, Source,
                H_AbsMag, G_Slope,
                Eccentricity, Perihelion_AU, Aphelion_AU, Inclination_deg,
                LongAscNode_deg, ArgPerihelion_deg, SemiMajorAxis_AU,
                MeanAnomaly_deg, MeanMotion_degPerDay, OrbitalPeriod_days,
                Epoch_JD, T_Perihelion_JD, JplHorizonsId, SbdbDesig, SortOrder,
                EphemerisMinJD, EphemerisMaxJD, EphemerisMinStr, EphemerisMaxStr
            ) VALUES (
                src.Slug, src.DisplayName, src.Kind, 1, 0, 'mpcorb',
                src.H_AbsMag, src.G_Slope,
                src.Eccentricity, src.Perihelion_AU, src.Aphelion_AU, src.Inclination_deg,
                src.LongAscNode_deg, src.ArgPerihelion_deg, src.SemiMajorAxis_AU,
                src.MeanAnomaly_deg, src.MeanMotion_degPerDay, src.OrbitalPeriod_days,
                src.Epoch_JD, src.T_Perihelion_JD, src.JplHorizonsId, src.SbdbDesig, src.SortOrder,
                2305426.499, 2634531.499, '1599-Dec-10 23:59', '2500-Dec-31 23:58'
            )
            OUTPUT $action;";

        await using var mergeCmd = new SqlCommand(merge, connection) { CommandTimeout = 600 };
        await using var reader = await mergeCmd.ExecuteReaderAsync(cancellationToken);

        int inserted = 0, updated = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == "INSERT") inserted++;
            else updated++;
        }

        return (inserted, updated);
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class MpcorbJsonRecord
    {
        public string? Number           { get; set; }   // e.g. "(433)" or null
        public string? Name             { get; set; }
        public string? Principal_desig  { get; set; }
        public double? Epoch            { get; set; }   // Julian Date (already numeric)
        public double  M                { get; set; }   // mean anomaly (deg)
        public double  Peri             { get; set; }   // arg of perihelion (deg)
        public double  Node             { get; set; }   // longitude of ascending node (deg)
        [JsonPropertyName("i")]
        public double  I                { get; set; }   // inclination (deg)
        [JsonPropertyName("e")]
        public double  E                { get; set; }   // eccentricity
        [JsonPropertyName("n")]
        public double  N                { get; set; }   // mean daily motion (deg/day)
        [JsonPropertyName("a")]
        public double  A                { get; set; }   // semi-major axis (AU)
        public double? H                { get; set; }   // absolute magnitude
        public double? G                { get; set; }   // slope parameter
        public string? Orbit_type       { get; set; }   // "Amor", "Apollo", "Aten", "Atira"
        public string? Hex_flags        { get; set; }
        public int?    PHA_flag         { get; set; }   // 1 if PHA, absent otherwise
        public double? Perihelion_dist  { get; set; }   // AU (pre-computed)
        public double? Aphelion_dist    { get; set; }   // AU (pre-computed)
        public double? Orbital_period   { get; set; }   // years (pre-computed)
        public double? Tp               { get; set; }   // time of perihelion (JD)
    }

    private readonly record struct MpcorbSeedRow(
        string   Slug,
        string   DisplayName,
        string   Kind,
        double?  H,
        double?  G,
        double   Eccentricity,
        double?  Perihelion_AU,
        double?  Aphelion_AU,
        double   Inclination_deg,
        double   LongAscNode_deg,
        double   ArgPerihelion_deg,
        double?  SemiMajorAxis_AU,
        double   MeanAnomaly_deg,
        double   MeanMotion_degPerDay,
        double?  OrbitalPeriod_days,
        double?  Epoch_JD,
        double?  T_Perihelion_JD,
        string?  JplHorizonsId,
        string?  SbdbDesig,
        int      SortOrder
    );
}
