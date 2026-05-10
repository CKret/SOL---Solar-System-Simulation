namespace Sol.Api.Data.Entities;

public class EphemerisSampleEntity
{
    public int     BodyId      { get; set; }
    public double  SampleJd    { get; set; }
    public double  X_AU        { get; set; }
    public double  Y_AU        { get; set; }
    public double  Z_AU        { get; set; }
    public double? VX_AUPerDay { get; set; }
    public double? VY_AUPerDay { get; set; }
    public double? VZ_AUPerDay { get; set; }
    public string? Frame       { get; set; }
    public string? Source      { get; set; }
    public DateTime CreatedUtc { get; set; }

    public BodyEntity Body     { get; set; } = default!;
}
