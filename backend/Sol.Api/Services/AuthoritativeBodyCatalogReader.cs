using Sol.Api.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sol.Api.Services;



public sealed partial class AuthoritativeBodyCatalogReader : IAuthoritativeBodyCatalogReader
{
  private const string HorizonsApiBase = "https://ssd.jpl.nasa.gov/api/horizons.api";
  private const string SbdbApiBase = "https://ssd-api.jpl.nasa.gov/sbdb.api";

  private readonly HttpClient _httpClient;
  private readonly JplEpochCoverageProvider _epochProvider;

  public AuthoritativeBodyCatalogReader(HttpClient httpClient)
  {
    _httpClient = httpClient;
    _epochProvider = new JplEpochCoverageProvider(httpClient);
  }

    public async Task<IReadOnlyList<CatalogBodySeed>> ReadBodiesAsync(CancellationToken cancellationToken)
    {
        var seeds = new List<CatalogBodySeed>();

        foreach (var target in AuthoritativeCatalogManifest.Targets.Where(target => target.HorizonsCommand is not null && target.SbdbDesignation is null))
        {
            seeds.Add(await ReadHorizonsSeedAsync(target, cancellationToken));
        }

        foreach (var target in AuthoritativeCatalogManifest.Targets.Where(target => target.SbdbDesignation is not null))
        {
            var seed = await ReadSbdbSeedAsync(target, cancellationToken);
            if (seed is not null) seeds.Add(seed);
        }
        return seeds;
    }

  private async Task<CatalogBodySeed> ReadHorizonsSeedAsync(AuthoritativeCatalogTarget target, CancellationToken cancellationToken)
  {

    // Fetch per-body epoch coverage from the JPL support API (cached after first call).
    await _epochProvider.EnsureInitializedAsync(cancellationToken);
    string? minEpoch = null, maxEpoch = null;
    double? minJD = null, maxJD = null;
    var idKey = target.HorizonsCommand?.Trim();
    if (idKey != null && _epochProvider.TryGetEpochRange(idKey) is { } epochRange)
    {
        (minJD, maxJD, minEpoch, maxEpoch) = epochRange;
    }

    // Fetch Horizons metadata for completeness (but do not attempt to parse epochs)
    var command = Uri.EscapeDataString($"'{target.HorizonsCommand}'");
    var requestUri = $"{HorizonsApiBase}?format=json&OBJ_DATA=YES&MAKE_EPHEM=NO&COMMAND={command}";
    using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    var resultText = document.RootElement.GetProperty("result").GetString()
      ?? throw new InvalidOperationException($"Horizons response did not include a result block for {target.DisplayName}.");

    // Defensive: ensure resultText is not null before splitting
    var lines = !string.IsNullOrEmpty(resultText)
        ? resultText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();
    string? targetLine = lines.FirstOrDefault(line => line.StartsWith("Target body name:", StringComparison.OrdinalIgnoreCase));
    // Defensive: targetLine may be null if not found

    // Robust JPL ID extraction:
    // - For planets/moons: parse numeric ID from header if available
    // - For spacecraft: use HorizonsCommand (negative number)
    // - For comets/asteroids: use HorizonsCommand (string)
    string? jplId = null;
    var headerLine = lines.FirstOrDefault(l => Regex.IsMatch(l, @"\s+\d+\s*/"));
    if (headerLine != null)
    {
        var match = Regex.Match(headerLine, @"\s(\d+)\s*/");
        if (match.Success)
            jplId = match.Groups[1].Value;
    }
    // If header parse fails, fallback to HorizonsCommand
    if (string.IsNullOrWhiteSpace(jplId))
    {
        // If HorizonsCommand is numeric (positive or negative), use as string
        if (int.TryParse(target.HorizonsCommand, out _))
        {
            jplId = target.HorizonsCommand;
        }
        else
        {
            // For comets/asteroids, use the string designation
            jplId = target.HorizonsCommand;
        }
    }

    // Orbital output variables (populated later by QAdProvider and Horizons ELEMENTS fetch)
    double? aphelion = null, perihelion = null, eccentricity = null, inclination = null, semimajor = null, argperi = null, longasc = null, meananom = null, meannotion = null, period = null, epochjd = null;

    // Physical properties parsed from Horizons OBJ_DATA result text
    var phys            = ParseHorizonsObjData(resultText);
    var density         = phys.Density_gcm3;
    var meanRadius      = phys.MeanRadius_km;
    var equatorialRadius= phys.EquatorialRadius_km;
    var mass            = phys.Mass_1e23kg;
    var volume          = phys.Volume_1e10km3;
    var gm              = phys.GM_km3s2;
    var massRatio       = phys.MassRatio;
    var momentOfInertia = phys.MomentOfInertia;
    var eqGravity       = phys.EqGravity_ms2;
    var coreRadius      = phys.CoreRadius_km;
    var albedo          = phys.GeometricAlbedo;
    var surfaceEmissivity = phys.SurfaceEmissivity;
    var meanTemp        = phys.MeanTemperature_K;
    var atmosPressure   = phys.AtmosPressure_bar;
    var maxAngDiam      = phys.MaxAngularDiam_arcsec;
    var visualMag       = phys.VisualMag;
    var obliquityArcmin = phys.ObliquityArcmin;
    var hillSphereRp    = phys.HillSphereRp;
    var siderealRotPeriod = phys.SiderealRotPeriod_d;
    var siderealRotRate  = phys.SiderealRotRate_radps;
    var meanSolarDay    = phys.MeanSolarDay_d;
    var sidOrbPeriodY   = phys.SidOrbPeriodY;
    var sidOrbPeriodD   = phys.SidOrbPeriodD;
    var escapeVelKms    = phys.EscapeVelKms;
    var meanOrbitVelKms = phys.MeanOrbitVelKms;
    var solarConstMean  = phys.SolarConstMean;
    var solarConstPeri  = phys.SolarConstPeri;
    var solarConstAph   = phys.SolarConstAph;
    var maxIRMean       = phys.MaxIRMean;
    var maxIRPeri       = phys.MaxIRPeri;
    var maxIRAph        = phys.MaxIRAph;
    var minIRMean       = phys.MinIRMean;
    var minIRPeri       = phys.MinIRPeri;
    var minIRAph        = phys.MinIRAph;

    // Integrate authoritative Q/Ad provider (overrides if present)
    var (qFromProvider, adFromProvider) = await QAdProvider.GetQAdAsync(
      target.DisplayName,
      null,
      target.HorizonsCommand,
      target.Category
    );
    if (qFromProvider.HasValue) perihelion = qFromProvider;
    if (adFromProvider.HasValue) aphelion = adFromProvider;

    // Fetch Keplerian orbital elements from Horizons ELEMENTS for planets and moons.
    // OBJ_DATA does not include classical elements; a separate MAKE_EPHEM=YES request is required.
    // Planets use the solar system barycenter; moons use their parent body as the center.
    if ((target.Kind == "planet" || target.Kind == "moon") && !string.IsNullOrWhiteSpace(target.HorizonsCommand))
    {
        var elemCenter = target.Kind == "moon" ? GetParentCenter(target.ParentSlug) : "500@0";
        if (elemCenter != null)
        {
            var elements = await FetchHorizonsElementsAsync(target.HorizonsCommand, elemCenter, cancellationToken);
            eccentricity ??= elements.Eccentricity;
            semimajor ??= elements.SemiMajorAxis_AU;
            inclination ??= elements.Inclination_deg;
            longasc ??= elements.LongAscNode_deg;
            argperi ??= elements.ArgPerihelion_deg;
            meannotion ??= elements.MeanMotion_degPerDay;
            meananom ??= elements.MeanAnomaly_deg;
            period ??= elements.OrbitalPeriod_days;
            epochjd ??= elements.Epoch_JD;
            sidOrbPeriodD ??= elements.OrbitalPeriod_days;
            if (target.Kind == "moon")
            {
                // QAdProvider returns barycentric (heliocentric) Q/AD for moons, which is the parent
                // planet's heliocentric distance — not the moon's periapsis/apoapsis around its parent.
                // Override with the parent-centric values from the ELEMENTS request.
                if (elements.Perihelion_AU.HasValue) perihelion = elements.Perihelion_AU;
                if (elements.Aphelion_AU.HasValue) aphelion = elements.Aphelion_AU;
            }
            else
            {
                perihelion ??= elements.Perihelion_AU;
                aphelion ??= elements.Aphelion_AU;
            }
        }
    }

    var metadata = new Dictionary<string, object?>
    {
        ["source"] = "JPL Horizons",
        ["command"] = target.HorizonsCommand,
        ["targetLine"] = targetLine,
        ["requestUri"] = requestUri,
        ["fetchedUtc"] = DateTime.UtcNow,
        ["authority"] = "remote",
        ["rawResult"] = resultText
    };

    // Normalize Kind from Category if missing
    string kind = target.Kind;
    if (string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(target.Category))
    {
        kind = target.Category.Trim().ToLowerInvariant().Replace(" ", "-");
    }

    return CreateSeed(
      slug: target.Slug,
      displayName: target.DisplayName,
      category: target.Category,
      kind: kind,
      parentSlug: target.ParentSlug,
      sortOrder: target.SortOrder,
      metadata: metadata,
      jplId: jplId,
      minEpoch: minEpoch,
      maxEpoch: maxEpoch,
      EphemerisMinJD: minJD,
      EphemerisMaxJD: maxJD,
      Source: "horizons",
      Aphelion_AU: aphelion,
      Perihelion_AU: perihelion,
      Eccentricity: eccentricity,
      Inclination_deg: inclination,
      SemiMajorAxis_AU: semimajor,
      ArgumentOfPerihelion_deg: argperi,
      LongitudeOfAscendingNode_deg: longasc,
      MeanAnomaly_deg: meananom,
      MeanMotion_degPerDay: meannotion,
      OrbitalPeriod_days: period,
      Epoch_JD: epochjd,
      MeanRadius_km: meanRadius ?? equatorialRadius,
      Density_gcm3: density,
      Mass_1e23kg: mass,
      Volume_1e10km3: volume,
      SiderealRotPeriod_d: siderealRotPeriod,
      SiderealRotRate_radps: siderealRotRate,
      MeanSolarDay_d: meanSolarDay,
      CoreRadius_km: coreRadius,
      GeometricAlbedo: albedo,
      SurfaceEmissivity: surfaceEmissivity,
      GM_km3s2: gm,
      EquatorialRadius_km: equatorialRadius,
      MassRatioSunPlanet: massRatio,
      MomentOfInertia: momentOfInertia,
      EquatorialGravity_ms2: eqGravity,
      AtmosPressure_bar: atmosPressure,
      MaxAngularDiam_arcsec: maxAngDiam,
      MeanTemperature_K: meanTemp,
      VisualMag: visualMag,
      ObliquityToOrbit_arcmin: obliquityArcmin,
      HillSphereRadius_Rp: hillSphereRp,
      SiderealOrbPeriod_y: sidOrbPeriodY,
      SiderealOrbPeriod_d: sidOrbPeriodD,
      EscapeVelocity_kms: escapeVelKms,
      MeanOrbitVelocity_kms: meanOrbitVelKms,
      SolarConstant_Wm2_Mean: solarConstMean,
      SolarConstant_Wm2_Perihelion: solarConstPeri,
      SolarConstant_Wm2_Aphelion: solarConstAph,
      MaxPlanetaryIR_Wm2_Mean: maxIRMean,
      MaxPlanetaryIR_Wm2_Perihelion: maxIRPeri,
      MaxPlanetaryIR_Wm2_Aphelion: maxIRAph,
      MinPlanetaryIR_Wm2_Mean: minIRMean,
      MinPlanetaryIR_Wm2_Perihelion: minIRPeri,
      MinPlanetaryIR_Wm2_Aphelion: minIRAph
    );
  }

  private async Task<CatalogBodySeed?> ReadSbdbSeedAsync(AuthoritativeCatalogTarget target, CancellationToken cancellationToken)
  {
    var designator = Uri.EscapeDataString(target.SbdbDesignation!);
    var requestUri = $"{SbdbApiBase}?des={designator}&phys-par=1&full-prec=1";
    using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

    if (!document.RootElement.TryGetProperty("object", out var objectElement)) {
      // Body not found in SBDB (e.g. some SL9 fragments were never catalogued separately).
      // Skip silently rather than crash the whole import.
      Console.Error.WriteLine($"SBDB: '{target.SbdbDesignation}' ({target.DisplayName}) not found — skipping.");
      return null;
    }

    var orbitClass = objectElement.TryGetProperty("orbit_class", out var orbitClassElement)
      ? orbitClassElement.TryGetProperty("name", out var orbitClassName) ? orbitClassName.GetString() : null
      : null;

    var physicalParameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (document.RootElement.TryGetProperty("phys_par", out var physParElement) && physParElement.ValueKind == JsonValueKind.Array) {
      foreach (var parameter in physParElement.EnumerateArray()) {
        if (!parameter.TryGetProperty("name", out var nameElement)) continue;
        physicalParameters[nameElement.GetString() ?? string.Empty] = parameter.TryGetProperty("value", out var valueElement)
          ? valueElement.GetString()
          : null;
      }
    }

    string? minEpoch = null, maxEpoch = null;
    double? minJD = null, maxJD = null;
    var spkid = GetOptionalString(objectElement, "spkid");
    if (!string.IsNullOrWhiteSpace(spkid))
    {
        var epochData = await _epochProvider.FetchAndCacheBodyAsync(spkid, cancellationToken);
        if (epochData is { } ed)
            (minJD, maxJD, minEpoch, maxEpoch) = ed;
    }

    // Parse orbital elements from SBDB orbit.elements array.
    // The objectElement only contains metadata (fullname, spkid, etc.); the orbital elements
    // live under the top-level "orbit" object as a named array of { name, value } entries.
    double? aphelion = null, perihelion = null, eccentricity = null, inclination = null;
    double? semimajor = null, argperi = null, longasc = null, meananom = null, meannotion = null;
    double? period = null, epochjd = null;

    if (document.RootElement.TryGetProperty("orbit", out var orbitElement))
    {
        if (orbitElement.TryGetProperty("epoch", out var epochProp))
        {
            var epochStr = epochProp.GetString();
            if (epochStr != null && double.TryParse(epochStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var ej))
                epochjd = ej;
        }

        if (orbitElement.TryGetProperty("elements", out var elementsArr) && elementsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in elementsArr.EnumerateArray())
            {
                if (!el.TryGetProperty("name", out var nameProp)) continue;
                if (!el.TryGetProperty("value", out var valueProp)) continue;
                var valStr = valueProp.GetString();
                if (valStr == null || !double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dval)) continue;
                switch (nameProp.GetString())
                {
                    case "e":   eccentricity = dval; break;
                    case "a":   semimajor    = dval; break;
                    case "q":   perihelion   = dval; break;  // perihelion distance
                    case "i":   inclination  = dval; break;
                    case "om":  longasc      = dval; break;
                    case "w":   argperi      = dval; break;
                    case "ma":  meananom     = dval; break;
                    case "per": period       = dval; break;
                    case "n":   meannotion   = dval; break;
                    case "ad":  aphelion     = dval; break;  // aphelion distance
                }
            }
        }
    }

    // Parse physical properties from SBDB phys_par entries.
    // Units are fixed per parameter name: diameter (km), density (g/cm^3), GM (km^3/s^2), rot_per (hours).
    double? GetPhysPar(string name)
    {
        if (!physicalParameters.TryGetValue(name, out var val) || val == null) return null;
        return double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
    }
    var sbdbDiameter_km = GetPhysPar("diameter");
    var sbdbMeanRadius_km = sbdbDiameter_km.HasValue ? sbdbDiameter_km / 2.0 : null;
    var sbdbAlbedo = GetPhysPar("albedo");
    var sbdbDensity = GetPhysPar("density");
    var sbdbGm = GetPhysPar("GM");
    var sbdbRotPer_hr = GetPhysPar("rot_per");  // hours
    var sbdbRotPer_d = sbdbRotPer_hr.HasValue ? Math.Abs(sbdbRotPer_hr.Value) / 24.0 : (double?)null;
    var sbdbH = GetPhysPar("H");
    var sbdbG = GetPhysPar("G");

    // Supplementary Horizons OBJ_DATA call: SBDB phys_par is sparse for some bodies (e.g. Pluto has
    // only H and rot_per in SBDB but a full PHYSICAL DATA section in Horizons). Fill gaps if present.
    // Prefer the DES={spkid}; command derived from spkid — guaranteed to work in the REST API.
    var sbdbHorizonsCmd = BuildSbdbHorizonsCommand(objectElement) ?? target.HorizonsCommand;
    if (!string.IsNullOrWhiteSpace(sbdbHorizonsCmd))
    {
      var horizonsObjText = await FetchHorizonsObjDataAsync(sbdbHorizonsCmd, cancellationToken);
      if (!string.IsNullOrEmpty(horizonsObjText))
      {
        var hPhys = ParseHorizonsObjData(horizonsObjText);
        sbdbMeanRadius_km ??= hPhys.MeanRadius_km ?? hPhys.EquatorialRadius_km;
        sbdbAlbedo        ??= hPhys.GeometricAlbedo;
        sbdbDensity       ??= hPhys.Density_gcm3;
        sbdbGm            ??= hPhys.GM_km3s2;
        sbdbRotPer_d      ??= hPhys.SiderealRotPeriod_d;
      }
    }

    var metadata = new Dictionary<string, object?>
    {
        ["source"] = "JPL Small-Body Database",
        ["designation"] = target.SbdbDesignation,
        ["fullname"] = GetOptionalString(objectElement, "fullname"),
        ["shortname"] = GetOptionalString(objectElement, "shortname"),
        ["spkid"] = GetOptionalString(objectElement, "spkid"),
        ["orbitClass"] = orbitClass,
        ["physicalParameters"] = physicalParameters,
        ["requestUri"] = requestUri,
        ["fetchedUtc"] = DateTime.UtcNow,
        ["authority"] = "remote",
        ["rawObject"] = objectElement.ToString(),
        ["rawResponse"] = document.RootElement.ToString()
    };

    return CreateSeed(
      slug: target.Slug,
      displayName: target.DisplayName,
      category: target.Category,
      kind: target.Kind,
      parentSlug: target.ParentSlug,
      sortOrder: target.SortOrder,
      metadata: metadata,
      jplId: BuildSbdbHorizonsCommand(objectElement) ?? target.HorizonsCommand ?? target.SbdbDesignation,
      minEpoch: minEpoch,
      maxEpoch: maxEpoch,
      EphemerisMinJD: minJD,
      EphemerisMaxJD: maxJD,
      Aphelion_AU: aphelion,
      Perihelion_AU: perihelion,
      Eccentricity: eccentricity,
      Inclination_deg: inclination,
      SemiMajorAxis_AU: semimajor,
      ArgumentOfPerihelion_deg: argperi,
      LongitudeOfAscendingNode_deg: longasc,
      MeanAnomaly_deg: meananom,
      MeanMotion_degPerDay: meannotion,
      OrbitalPeriod_days: period,
      Epoch_JD: epochjd,
      MeanRadius_km: sbdbMeanRadius_km,
      GeometricAlbedo: sbdbAlbedo,
      Density_gcm3: sbdbDensity,
      GM_km3s2: sbdbGm,
      SiderealRotPeriod_d: sbdbRotPer_d,
      H_AbsMag: sbdbH,
      G_Slope: sbdbG,
      Source: "sbdb",
      SbdbDesignation: target.SbdbDesignation
    );
  }

  private static string? GetParentCenter(string? parentSlug) => parentSlug switch
  {
    "earth"   => "500@399",
    "mars"    => "500@499",
    "jupiter" => "500@599",
    "saturn"  => "500@699",
    "uranus"  => "500@799",
    "neptune" => "500@899",
    "pluto"   => "500@999",
    _         => null
  };

  private async Task<HorizonsOrbitalElements> FetchHorizonsElementsAsync(
    string command, string center, CancellationToken cancellationToken)
  {
    var cmd   = Uri.EscapeDataString($"'{command}'");
    var start = Uri.EscapeDataString("'2000-01-01 12:00'");
    var stop  = Uri.EscapeDataString("'2000-01-02 12:00'");
    var step  = Uri.EscapeDataString("'1 d'");
    var uri = $"{HorizonsApiBase}?format=json&COMMAND={cmd}&OBJ_DATA='NO'&MAKE_EPHEM='YES'&EPHEM_TYPE='ELEMENTS'&CENTER='{center}'&REF_PLANE='ECLIPTIC'&REF_SYSTEM='ICRF'&OUT_UNITS='AU-D'&TIME_TYPE='TDB'&START_TIME={start}&STOP_TIME={stop}&STEP_SIZE={step}";

    using var response = await _httpClient.GetAsync(uri, cancellationToken);
    if (!response.IsSuccessStatusCode)
      return default;

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    if (!document.RootElement.TryGetProperty("result", out var resultProp))
      return default;

    var resultText = resultProp.GetString();
    return string.IsNullOrEmpty(resultText) ? default : ParseHorizonsElements(resultText);
  }

  private static HorizonsOrbitalElements ParseHorizonsElements(string resultText)
  {
    var soe = resultText.IndexOf("$$SOE", StringComparison.Ordinal);
    var eoe = resultText.IndexOf("$$EOE", StringComparison.Ordinal);
    if (soe < 0 || eoe < 0)
      return default;

    var block = resultText.Substring(soe, eoe - soe);

    static double? M(string text, string pattern)
    {
      var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
      return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        ? v : null;
    }

    const string num = @"([+-]?[\d.]+(?:[eE][+-]?\d+)?)";

    var epochM = Regex.Match(block, @"(\d{7,}\.\d+)\s*=\s*A\.D\.");
    double? epochJd = epochM.Success && double.TryParse(epochM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ej) ? ej : null;

    return new HorizonsOrbitalElements(
      Eccentricity:        M(block, $@"\bEC=\s*{num}"),
      Perihelion_AU:       M(block, $@"\bQR=\s*{num}"),
      Inclination_deg:     M(block, $@"\bIN=\s*{num}"),
      LongAscNode_deg:     M(block, $@"\bOM=\s*{num}"),
      ArgPerihelion_deg:   M(block, $@"\bW\s*=\s*{num}"),
      MeanMotion_degPerDay:M(block, $@"\bN\s*=\s*{num}"),
      MeanAnomaly_deg:     M(block, $@"\bMA=\s*{num}"),
      SemiMajorAxis_AU:    M(block, $@"\bA\s*=\s*{num}"),
      Aphelion_AU:         M(block, $@"\bAD=\s*{num}"),
      OrbitalPeriod_days:  M(block, $@"\bPR=\s*{num}"),
      Epoch_JD: epochJd
    );
  }

  private readonly record struct HorizonsOrbitalElements(
    double? Eccentricity,
    double? Perihelion_AU,
    double? Inclination_deg,
    double? LongAscNode_deg,
    double? ArgPerihelion_deg,
    double? MeanMotion_degPerDay,
    double? MeanAnomaly_deg,
    double? SemiMajorAxis_AU,
    double? Aphelion_AU,
    double? OrbitalPeriod_days,
    double? Epoch_JD);

  private async Task<string?> FetchHorizonsObjDataAsync(string horizonsCommand, CancellationToken cancellationToken)
  {
    var command = Uri.EscapeDataString($"'{horizonsCommand}'");
    var uri = $"{HorizonsApiBase}?format=json&OBJ_DATA=YES&MAKE_EPHEM=NO&COMMAND={command}";
    using var response = await _httpClient.GetAsync(uri, cancellationToken);
    if (!response.IsSuccessStatusCode) return null;
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    return document.RootElement.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;
  }

  private static HorizonsPhysicalData ParseHorizonsObjData(string resultText)
  {
    double? Ext(params string[] labels)
    {
      foreach (var label in labels)
      {
        var idx = resultText.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) continue;
        var segment = resultText.Substring(idx + label.Length, Math.Min(120, resultText.Length - idx - label.Length));
        var m = Regex.Match(segment, @"[=]\s*~?\s*([+-]?\d[\d.]*(?:[eE][+-]?\d+)?)");
        if (!m.Success) continue;
        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
          return v;
      }
      return null;
    }

    (double? val, string? unit) ExtU(params string[] labels)
    {
      foreach (var label in labels)
      {
        var idx = resultText.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) continue;
        var segment = resultText.Substring(idx + label.Length, Math.Min(120, resultText.Length - idx - label.Length));
        var m = Regex.Match(segment, @"[=]\s*~?\s*([+-]?\d[\d.]*(?:[eE][+-]?\d+)?)(?:\s*[+-][+-]?[\d.]+)?\s*([a-zA-Z]+)?");
        if (!m.Success) continue;
        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
          return (v, m.Groups[2].Success ? m.Groups[2].Value : null);
      }
      return (null, null);
    }

    double? ExtTable(string rowLabel, int col)
    {
      var idx = resultText.IndexOf(rowLabel, StringComparison.OrdinalIgnoreCase);
      if (idx < 0) return null;
      var lineEnd = resultText.IndexOf('\n', idx);
      var segment = lineEnd > idx
        ? resultText.Substring(idx + rowLabel.Length, lineEnd - idx - rowLabel.Length)
        : resultText.Substring(idx + rowLabel.Length, Math.Min(80, resultText.Length - idx - rowLabel.Length));
      var nums = Regex.Matches(segment, @"\b\d+\.?\d*(?:[eE][+-]?\d+)?\b");
      return nums.Count > col && double.TryParse(nums[col].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    }

    var density = Ext("Density (g cm^-3)", "Density (g/cm^3)", "Mean density, g/cm^3", "Density, g/cm^3");
    // "Density (R=1195 km) = 1.86 g/cm^3" (Pluto) — standard label search finds the wrong '=' inside the parens
    if (!density.HasValue)
    {
      var dm = Regex.Match(resultText, @"Density\s*\([^)]+\)\s*=\s*([\d.]+)\s*g/cm", RegexOptions.IgnoreCase);
      if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
        density = dv;
    }

    var meanRadius = Ext(
      "Vol. mean radius (km)", "Vol. Mean Radius (km)", "Vol. mean radius, km",
      "Mean radius (km)", "Mean Radius (km)",
      "Radius (km)");   // small moons: "Radius (km) = 13.1 x11.1 x9.3" — extracts the first (largest) axis
    var equatorialRadius = Ext(
      "Equ. radius, km", "Equatorial radius (km)", "Equatorial radius, Re",
      "Equat. radius (1 bar)", "Radius (photosphere)");
    // Mass: extract with unit-aware scaling to 10^23 kg.
    // Horizons uses labels like "Mass x10^24 (kg)" or "Mass x 10^26 (kg)"; we must scale.
    static double? ScaleMass(double? v, int exp) =>
        v.HasValue ? v.Value * Math.Pow(10.0, exp - 23) : null;
    var mass =
        ScaleMass(Ext("Mass x10^20 (kg)", "Mass x 10^20 (kg)", "Mass, x10^20 kg"), 20) ??
        ScaleMass(Ext("Mass x10^21 (kg)", "Mass x 10^21 (kg)", "Mass, x10^21 kg"), 21) ??
        ScaleMass(Ext("Mass x10^22 (kg)", "Mass x 10^22 (kg)", "Mass, x10^22 kg"), 22) ??
        ScaleMass(Ext("Mass x10^23 (kg)", "Mass x 10^23 (kg)", "Mass, x10^23 kg"), 23) ??
        ScaleMass(Ext("Mass x10^24 (kg)", "Mass x 10^24 (kg)", "Mass, x10^24 kg", "Mass, 10^24 kg"), 24) ??
        ScaleMass(Ext("Mass x10^25 (kg)", "Mass x 10^25 (kg)", "Mass, x10^25 kg"), 25) ??
        ScaleMass(Ext("Mass x10^26 (kg)", "Mass x 10^26 (kg)", "Mass, x10^26 kg"), 26);
    var volume = Ext("Volume (x10^10 km^3)", "Volume (km^3 x 10^10)", "Volume, 10^10 km^3");
    var gm     = Ext("GM (km^3/s^2)", "GM, km^3/s^2", "GM (planet) km^3/s^2");
    var massRatio      = Ext("Mass ratio (Sun/plnt)", "Mass ratio (Sun/Mars)", "Mass ratio (Sun/Moon)",
                             "Mass ratio (Sun/Sat)", "Mass ratio (Sun/Jup)", "Mass ratio (Sun/Ura)", "Mass ratio (Sun/Nep)");
    var momentOfInertia= Ext("Mom. of Inertia", "Moment of inertia");
    var eqGravity      = Ext("Equ. grav, ge (m/s^2)", "Equ. gravity  m/s^2", "Equ. gravity m/s^2",
                             "g_e, m/s^2  (equatorial)", "Surface gravity",
                             "Mean surf. gravity m/s^2", "Gravity m/s^2, equat.");
    var coreRadius     = Ext("Core radius (km)", "Fluid core rad", "Inner core rad");
    var albedo         = Ext("Geometric Albedo", "Geometric albedo");
    var surfaceEmissivity = Ext("Surface emissivity");
    var meanTemp       = Ext("Mean temperature (K)", "Mean Temperature (K)", "Mean surface temp (Ts), K",
                             "Atmos. temp. (1 bar)", "Effective temp, K",
                             "Surface temp.(K),T_m", "Surface temp.(K), T_m",
                             "Surface temperature, K", "Mean temperature");
    var atmosPressure  = Ext("Atmos. pressure (bar)", "Atmospheric pressure (bar)", "Atm. pressure");
    var maxAngDiam     = Ext("Max. angular diam.", "Maximum angular diam.", "Angular diam at 1 AU");
    var visualMag      = Ext("Visual mag. V(1,0)", "Vis. magnitude V(1,0)", "Vis. mag. V(1,0)",
                             "Visual magnitude V(1,0)", "V(1,0)");
    // Obliquity: Horizons uses arcminutes (') for Mercury, degrees for others.
    // Normalize everything to arcminutes stored in ObliquityToOrbit_arcmin.
    double? obliquityArcmin = null;
    {
        foreach (var oblLabel in new[] { "Obliquity to orbit", "Obliquity to ecliptic" })
        {
            var oi = resultText.IndexOf(oblLabel, StringComparison.OrdinalIgnoreCase);
            if (oi < 0) continue;
            var seg = resultText.Substring(oi, Math.Min(120, resultText.Length - oi));
            var om = Regex.Match(seg, @"[=]\s*~?\s*([\d.]+)\s*(['""]|deg\b)?", RegexOptions.IgnoreCase);
            if (!om.Success || !double.TryParse(om.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oval)) continue;
            var ounit = om.Groups[2].Value;
            obliquityArcmin = ounit == "'" ? oval : oval * 60.0;
            break;
        }
    }
    var hillSphereRp   = Ext("Hill's sphere rad. Rp", "Hill's sphere rad.", "Hill's sphere radius", "Hill sphere rad. Rp");
    var escapeVelKms   = Ext("Escape vel. km/s", "Escape vel., km/s", "Escape speed, km/s",
                             "Escape velocity, km/s", "Escape velocity");
    var meanOrbitVelKms= Ext("Mean Orbit vel.  km/s", "Mean Orbit vel. km/s", "Mean orbit velocity",
                             "Mean orbit speed, km/s", "Orbital speed,  km/s", "Orbital speed, km/s");
    var siderealRotRate= Ext("Sid. rot. rate (rad/s)", "Sid. rot. rate, rad/s", "Sidereal rotation rate",
                             "Rot. Rate (rad/s)", "Sid. rot. rat, rad/s");

    // Sidereal rotation period: gas giants use HMS ("Xh Ym Z.Zs"), Earth uses label-encoded hours,
    // most others use decimal value with unit (h/hr/d) following.
    double? siderealRotPeriod;
    var hmsM = Regex.Match(resultText, @"Sid\.\s*rot\.\s*period[^=\n]*=\s*(\d+)h\s*(\d+)m\s*([\d.]+)", RegexOptions.IgnoreCase);
    if (hmsM.Success &&
        double.TryParse(hmsM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rH) &&
        double.TryParse(hmsM.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rM) &&
        double.TryParse(hmsM.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rS))
    {
      siderealRotPeriod = (rH + rM / 60.0 + rS / 3600.0) / 24.0;
    }
    else
    {
      var earthSidDay = Ext("Mean sidereal day, hr");
      if (earthSidDay.HasValue)
      {
        siderealRotPeriod = earthSidDay / 24.0;
      }
      else
      {
        var (rotVal, rotUnit) = ExtU(
          "Sidereal rot. period", "Sidereal rot.period",
          "Sid. rot. period",     "Sid. rot.period",
          "Adopted sid. rot. per.", "Sidereal rotation period");
        siderealRotPeriod = rotVal.HasValue
          ? (rotUnit?.StartsWith("h", StringComparison.OrdinalIgnoreCase) == true ? rotVal / 24.0 : rotVal)
          : null;
      }
    }

    // Mean solar day
    double? meanSolarDay;
    {
      var (hrsVal, _) = ExtU("Mean solar day, hrs");
      if (!hrsVal.HasValue) { var (hVal, _) = ExtU("Mean solar day, h"); hrsVal = hVal; }
      if (hrsVal.HasValue)
      {
        meanSolarDay = hrsVal / 24.0;
      }
      else
      {
        var (solVal, solUnit) = ExtU("Mean solar day (sol)");
        if (solVal.HasValue)
          meanSolarDay = string.Equals(solUnit, "s", StringComparison.OrdinalIgnoreCase) ? solVal / 86400.0 : solVal / 24.0;
        else
        {
          var earthVal = Ext("Mean solar day 2000.0");
          meanSolarDay = earthVal.HasValue ? earthVal / 86400.0 : Ext("Mean solar day");
        }
      }
    }

    // Sidereal orbital period
    var sidYm = Regex.Match(resultText,
      @"(?:Sidereal orb\. per\.|Mean sidereal orb per|Sidereal orb\. period|Sidereal orbit period|Sidereal orb period)\s*=\s*([\d.]+)\s*yr?",
      RegexOptions.IgnoreCase);
    var sidOrbPeriodY = sidYm.Success && double.TryParse(sidYm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var soy)
      ? soy : (double?)null;
    var sidDm = Regex.Match(resultText,
      @"(?:Sidereal orb\. per\.|Mean sidereal orb per|Sidereal orb\. period|Sidereal orbit period|Sidereal orb period)\s*=\s*([\d.]+)\s*d",
      RegexOptions.IgnoreCase);
    var sidOrbPeriodD = sidDm.Success && double.TryParse(sidDm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sod)
      ? sod : (double?)null;
    sidOrbPeriodD ??= Ext("Orbit period, d", "Orbital period, d");

    // Solar Constant: two formats.
    // Table (Mercury, gas giants): header line "Perihelion  Aphelion  Mean" → col 0=Peri, 1=Aph, 2=Mean.
    // Inline (Earth, some terrestrial): "= 1367.6 (mean), 1414 (perihelion), 1322 (aphelion)" → labeled.
    double? solarConstPeri, solarConstAph, solarConstMean;
    {
        var scIdx = resultText.IndexOf("Solar Constant (W/m^2)", StringComparison.OrdinalIgnoreCase);
        if (scIdx < 0)
        {
            solarConstPeri = solarConstAph = solarConstMean = null;
        }
        else
        {
            var scEnd = resultText.IndexOf('\n', scIdx);
            var scLine = scEnd > scIdx
                ? resultText.Substring(scIdx, scEnd - scIdx)
                : resultText.Substring(scIdx, Math.Min(200, resultText.Length - scIdx));
            var mMean = Regex.Match(scLine, @"([\d.]+)\s*\(mean\)", RegexOptions.IgnoreCase);
            var mPeri = Regex.Match(scLine, @"([\d.]+)\s*\(perihelion\)", RegexOptions.IgnoreCase);
            var mAph  = Regex.Match(scLine, @"([\d.]+)\s*\(aphelion\)", RegexOptions.IgnoreCase);
            if (mMean.Success || mPeri.Success || mAph.Success)
            {
                solarConstMean = mMean.Success && double.TryParse(mMean.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scM) ? scM : null;
                solarConstPeri = mPeri.Success && double.TryParse(mPeri.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scP) ? scP : null;
                solarConstAph  = mAph.Success  && double.TryParse(mAph.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out var scA) ? scA  : null;
            }
            else
            {
                solarConstPeri = ExtTable("Solar Constant (W/m^2)", 0);
                solarConstAph  = ExtTable("Solar Constant (W/m^2)", 1);
                solarConstMean = ExtTable("Solar Constant (W/m^2)", 2);
            }
        }
    }
    var maxIRPeri = ExtTable("Maximum Planetary IR (W/m^2)", 0);
    var maxIRAph  = ExtTable("Maximum Planetary IR (W/m^2)", 1);
    var maxIRMean = ExtTable("Maximum Planetary IR (W/m^2)", 2);
    var minIRPeri = ExtTable("Minimum Planetary IR (W/m^2)", 0);
    var minIRAph  = ExtTable("Minimum Planetary IR (W/m^2)", 1);
    var minIRMean = ExtTable("Minimum Planetary IR (W/m^2)", 2);

    // Derive mass from GM when not directly stated (M = GM/G; G=6.674e-20 km³/(kg·s²) → M_1e23kg = GM/6674)
    if (!mass.HasValue && gm.HasValue)
        mass = gm.Value / 6674.0;
    // Derive escape velocity from GM and radius when not stated (v = √(2GM/R))
    var _physR = meanRadius ?? equatorialRadius;
    if (!escapeVelKms.HasValue && gm.HasValue && _physR.HasValue && _physR.Value > 0)
        escapeVelKms = Math.Sqrt(2.0 * gm.Value / _physR.Value);

    return new HorizonsPhysicalData(
      Density_gcm3:          density,
      MeanRadius_km:         meanRadius,
      EquatorialRadius_km:   equatorialRadius,
      Mass_1e23kg:           mass,
      Volume_1e10km3:        volume,
      GM_km3s2:              gm,
      MassRatio:             massRatio,
      MomentOfInertia:       momentOfInertia,
      EqGravity_ms2:         eqGravity,
      CoreRadius_km:         coreRadius,
      GeometricAlbedo:       albedo,
      SurfaceEmissivity:     surfaceEmissivity,
      MeanTemperature_K:     meanTemp,
      AtmosPressure_bar:     atmosPressure,
      MaxAngularDiam_arcsec: maxAngDiam,
      VisualMag:             visualMag,
      ObliquityArcmin:       obliquityArcmin,
      HillSphereRp:          hillSphereRp,
      SiderealRotPeriod_d:   siderealRotPeriod,
      SiderealRotRate_radps: siderealRotRate,
      MeanSolarDay_d:        meanSolarDay,
      SidOrbPeriodY:         sidOrbPeriodY,
      SidOrbPeriodD:         sidOrbPeriodD,
      EscapeVelKms:          escapeVelKms,
      MeanOrbitVelKms:       meanOrbitVelKms,
      SolarConstMean:        solarConstMean,
      SolarConstPeri:        solarConstPeri,
      SolarConstAph:         solarConstAph,
      MaxIRMean:             maxIRMean,
      MaxIRPeri:             maxIRPeri,
      MaxIRAph:              maxIRAph,
      MinIRMean:             minIRMean,
      MinIRPeri:             minIRPeri,
      MinIRAph:              minIRAph);
  }

  private readonly record struct HorizonsPhysicalData(
    double? Density_gcm3,
    double? MeanRadius_km,
    double? EquatorialRadius_km,
    double? Mass_1e23kg,
    double? Volume_1e10km3,
    double? GM_km3s2,
    double? MassRatio,
    double? MomentOfInertia,
    double? EqGravity_ms2,
    double? CoreRadius_km,
    double? GeometricAlbedo,
    double? SurfaceEmissivity,
    double? MeanTemperature_K,
    double? AtmosPressure_bar,
    double? MaxAngularDiam_arcsec,
    double? VisualMag,
    double? ObliquityArcmin,
    double? HillSphereRp,
    double? SiderealRotPeriod_d,
    double? SiderealRotRate_radps,
    double? MeanSolarDay_d,
    double? SidOrbPeriodY,
    double? SidOrbPeriodD,
    double? EscapeVelKms,
    double? MeanOrbitVelKms,
    double? SolarConstMean,
    double? SolarConstPeri,
    double? SolarConstAph,
    double? MaxIRMean,
    double? MaxIRPeri,
    double? MaxIRAph,
    double? MinIRMean,
    double? MinIRPeri,
    double? MinIRAph);

  private static string? GetOptionalString(JsonElement element, string propertyName)
  {
    return element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
  }

  private static string? BuildSbdbHorizonsCommand(JsonElement objectElement)
  {
    var spkid = GetOptionalString(objectElement, "spkid");
    return string.IsNullOrWhiteSpace(spkid) ? null : $"DES={spkid};";
  }


  private static CatalogBodySeed CreateSeed(
        string slug,
        string displayName,
        string category,
        string kind,
        string? parentSlug,
        int sortOrder,
        object metadata,
        string? jplId = null,
        string? minEpoch = null,
        string? maxEpoch = null,
        double? Aphelion_AU = null,
        double? Perihelion_AU = null,
        double? Eccentricity = null,
        double? Inclination_deg = null,
        double? SemiMajorAxis_AU = null,
        double? ArgumentOfPerihelion_deg = null,
        double? LongitudeOfAscendingNode_deg = null,
        double? MeanAnomaly_deg = null,
        double? MeanMotion_degPerDay = null,
        double? OrbitalPeriod_days = null,
        double? Epoch_JD = null,
        double? MeanRadius_km = null,
        double? Density_gcm3 = null,
        double? Mass_1e23kg = null,
        double? Volume_1e10km3 = null,
        double? SiderealRotPeriod_d = null,
        double? SiderealRotRate_radps = null,
        double? MeanSolarDay_d = null,
        double? CoreRadius_km = null,
        double? GeometricAlbedo = null,
        double? SurfaceEmissivity = null,
        double? GM_km3s2 = null,
        double? EquatorialRadius_km = null,
        double? MassRatioSunPlanet = null,
        double? MomentOfInertia = null,
        double? EquatorialGravity_ms2 = null,
        double? AtmosPressure_bar = null,
        double? MaxAngularDiam_arcsec = null,
        double? MeanTemperature_K = null,
        double? VisualMag = null,
        double? ObliquityToOrbit_arcmin = null,
        double? HillSphereRadius_Rp = null,
        double? SiderealOrbPeriod_y = null,
        double? SiderealOrbPeriod_d = null,
        double? EscapeVelocity_kms = null,
        double? MeanOrbitVelocity_kms = null,
        double? SolarConstant_Wm2_Mean = null,
        double? SolarConstant_Wm2_Perihelion = null,
        double? SolarConstant_Wm2_Aphelion = null,
        double? MaxPlanetaryIR_Wm2_Mean = null,
        double? MaxPlanetaryIR_Wm2_Perihelion = null,
        double? MaxPlanetaryIR_Wm2_Aphelion = null,
        double? MinPlanetaryIR_Wm2_Mean = null,
        double? MinPlanetaryIR_Wm2_Perihelion = null,
        double? MinPlanetaryIR_Wm2_Aphelion = null,
        double? H_AbsMag = null,
        double? G_Slope = null,
        string? Source = null,
        string? SbdbDesignation = null,
        double? EphemerisMinJD = null,
        double? EphemerisMaxJD = null
      )
      {
        return new CatalogBodySeed(
          slug,
          displayName,
          category,
          kind,
          parentSlug,
          sortOrder,
          JsonSerializer.Serialize(metadata),
          jplId,
          minEpoch,
          maxEpoch,
          Aphelion_AU,
          Perihelion_AU,
          Eccentricity,
          Inclination_deg,
          SemiMajorAxis_AU,
          ArgumentOfPerihelion_deg,
          LongitudeOfAscendingNode_deg,
          MeanAnomaly_deg,
          MeanMotion_degPerDay,
          OrbitalPeriod_days,
          Epoch_JD,
          MeanRadius_km,
          Density_gcm3,
          Mass_1e23kg,
          Volume_1e10km3,
          SiderealRotPeriod_d,
          SiderealRotRate_radps,
          MeanSolarDay_d,
          CoreRadius_km,
          GeometricAlbedo,
          SurfaceEmissivity,
          GM_km3s2,
          EquatorialRadius_km,
          MassRatioSunPlanet,
          MomentOfInertia,
          EquatorialGravity_ms2,
          AtmosPressure_bar,
          MaxAngularDiam_arcsec,
          MeanTemperature_K,
          VisualMag,
          ObliquityToOrbit_arcmin,
          HillSphereRadius_Rp,
          SiderealOrbPeriod_y,
          SiderealOrbPeriod_d,
          EscapeVelocity_kms,
          MeanOrbitVelocity_kms,
          SolarConstant_Wm2_Mean,
          SolarConstant_Wm2_Perihelion,
          SolarConstant_Wm2_Aphelion,
          MaxPlanetaryIR_Wm2_Mean,
          MaxPlanetaryIR_Wm2_Perihelion,
          MaxPlanetaryIR_Wm2_Aphelion,
          MinPlanetaryIR_Wm2_Mean,
          MinPlanetaryIR_Wm2_Perihelion,
          MinPlanetaryIR_Wm2_Aphelion,
          H_AbsMag,
          G_Slope,
          Source,
          SbdbDesignation,
          EphemerisMinJD,
          EphemerisMaxJD
        );
      }
}