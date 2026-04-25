using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IEphemerisRepository
{
  Task<IReadOnlyList<CelestialBodySummary>> GetBodiesAsync(CancellationToken cancellationToken);
  Task<CelestialBodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken);
  Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken);
}