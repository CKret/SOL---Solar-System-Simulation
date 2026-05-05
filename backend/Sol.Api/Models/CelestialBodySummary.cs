namespace Sol.Api.Models;

public sealed record BodySummary(
  int Id,
  string Slug,
  string Name,
  string Kind,
  int? ParentBodyId,
  int SortOrder,
  string? JplHorizonsId,
  string? SbdbDesig,
  double? H_AbsMag,
  bool HasEphemeris,
  double? EphemerisMinJD,
  double? EphemerisMaxJD,
  string? EphemerisMinStr,
  string? EphemerisMaxStr,
  // Keplerian orbital elements
  double? Eccentricity,
  double? Perihelion_AU,
  double? Aphelion_AU,
  double? Inclination_deg,
  double? LongAscNode_deg,
  double? ArgPerihelion_deg,
  double? SemiMajorAxis_AU,
  double? MeanAnomaly_deg,
  double? MeanMotion_degPerDay,
  double? OrbitalPeriod_days,
  double? Epoch_JD,
  double? T_Perihelion_JD,
  // Simulation-critical physical properties
  double? GM_km3s2,
  double? MeanRadius_km,
  double? EquatorialRadius_km,
  double? Mass_1e23kg
);
