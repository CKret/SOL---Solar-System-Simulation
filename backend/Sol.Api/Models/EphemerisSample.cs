namespace Sol.Api.Models;

public sealed record EphemerisSample(
  int BodyId,
  double SampleJd,
  double X,
  double Y,
  double Z,
  double? Vx,
  double? Vy,
  double? Vz,
  string? Frame
);