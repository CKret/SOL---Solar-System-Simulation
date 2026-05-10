namespace Sol.Api.Data.Entities;

public class BodyEntity
{
    public int     BodyId               { get; set; }
    public string  Slug                 { get; set; } = default!;
    public string  DisplayName          { get; set; } = default!;
    public string  Kind                 { get; set; } = default!;
    public int?    ParentBodyId         { get; set; }
    public int     SortOrder            { get; set; }
    public bool    IsActive             { get; set; }
    public string? Source               { get; set; }
    public string? JplHorizonsId        { get; set; }
    public string? SbdbDesig            { get; set; }
    public double? H_AbsMag             { get; set; }
    public double? G_Slope              { get; set; }
    public bool    HasEphemeris         { get; set; }
    public bool    CompletedEphemeris   { get; set; }
    public double? EphemerisMinJD       { get; set; }
    public double? EphemerisMaxJD       { get; set; }
    public string? EphemerisMinStr      { get; set; }
    public string? EphemerisMaxStr      { get; set; }
    public double? Eccentricity         { get; set; }
    public double? Perihelion_AU        { get; set; }
    public double? Aphelion_AU          { get; set; }
    public double? Inclination_deg      { get; set; }
    public double? LongAscNode_deg      { get; set; }
    public double? ArgPerihelion_deg    { get; set; }
    public double? SemiMajorAxis_AU     { get; set; }
    public double? MeanAnomaly_deg      { get; set; }
    public double? MeanMotion_degPerDay { get; set; }
    public double? OrbitalPeriod_days   { get; set; }
    public double? Epoch_JD             { get; set; }
    public double? T_Perihelion_JD      { get; set; }
    public double? GM_km3s2             { get; set; }
    public double? MeanRadius_km        { get; set; }
    public double? EquatorialRadius_km  { get; set; }
    public double? Mass_1e23kg          { get; set; }
    public string? PhysicsJson          { get; set; }
    public DateTime CreatedUtc          { get; set; }
    public DateTime UpdatedUtc          { get; set; }

    public BodyEntity? Parent           { get; set; }
}
