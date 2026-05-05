namespace Sol.Api.Models;

public sealed record EphemerisRangeResponse(
  int BodyId,
  string? Slug,
  DateTime StartUtc,
  DateTime EndUtc,
  int Count,
  IReadOnlyList<EphemerisSample> Samples
);