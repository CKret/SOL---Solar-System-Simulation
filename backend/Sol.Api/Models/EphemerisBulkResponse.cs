namespace Sol.Api.Models;

public sealed record EphemerisBulkResponse(
  DateTime StartUtc,
  DateTime EndUtc,
  int Count,
  IReadOnlyList<EphemerisSample> Samples
);
