using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IEphemerisRepository
{
  Task<IReadOnlyList<BodySummary>> GetBodiesAsync(double? hMax, int? maxBodies, CancellationToken cancellationToken);
  Task<IReadOnlyList<BodySummary>> GetBodiesBatchAsync(double? hMinExclusive, double? hMaxInclusive, int take, int? afterBodyId, CancellationToken cancellationToken);
  Task<IReadOnlyList<BodySummary>> SearchBodiesAsync(string? query, int limit, bool completedEphemerisOnly, bool namedOnly, CancellationToken cancellationToken);
  Task<BodySummary?> GetBodyBySlugAsync(string slug, CancellationToken cancellationToken);
  Task<IReadOnlyList<EphemerisSample>> GetSamplesByBodyIdAsync(int bodyId, DateTime startUtc, DateTime endUtc, int limit, CancellationToken cancellationToken);
  Task<IReadOnlyList<EphemerisSample>> GetBulkSamplesAsync(DateTime startUtc, DateTime endUtc, double? hMax, int step, int? maxBodies, CancellationToken cancellationToken);
}
