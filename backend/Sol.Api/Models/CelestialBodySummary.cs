namespace Sol.Api.Models;

public sealed record CelestialBodySummary(
  int Id,
  string Slug,
  string Name,
  string? Category
);