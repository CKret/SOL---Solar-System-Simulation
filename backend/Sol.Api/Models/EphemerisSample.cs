namespace Sol.Api.Models;

public sealed record EphemerisSample(
  int BodyId,
  DateTime SampleTimeUtc,
  double X,
  double Y,
  double Z,
  double? Vx,
  double? Vy,
  double? Vz,
  string? Frame
);