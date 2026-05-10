namespace Sol.Api.Data.Entities;

public class EphemerisImportLogEntity
{
    public int      BodyId      { get; set; }
    public double   StartJd     { get; set; }
    public double   EndJd       { get; set; }
    public int      SampleCount { get; set; }
    public DateTime ImportedUtc { get; set; }

    public BodyEntity Body      { get; set; } = default!;
}
