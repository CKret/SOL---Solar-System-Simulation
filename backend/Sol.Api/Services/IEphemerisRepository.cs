using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IEphemerisRepository
{
  Task<IReadOnlyList<BodySummary>> GetBodiesAsync(double? hMax, CancellationToken cancellationToken);
  Task<BodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken);
  Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken);
}
