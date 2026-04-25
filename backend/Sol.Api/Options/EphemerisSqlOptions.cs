namespace Sol.Api.Options;

public sealed class EphemerisSqlOptions
{
  public const string SectionName = "EphemerisSql";

  public string BodiesQuery { get; init; } = string.Empty;
  public string BodyBySlugQuery { get; init; } = string.Empty;
  public string SamplesRangeQuery { get; init; } = string.Empty;
}