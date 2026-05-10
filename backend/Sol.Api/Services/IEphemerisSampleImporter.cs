using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IEphemerisSampleImporter
{
  Task<EphemerisSampleImportResult> ImportAsync(double? hMax, DateTime? startUtc, DateTime? endUtc, TimeSpan? sampleRateOverride, IReadOnlyList<string>? slugFilter, IReadOnlyList<int>? bodyIdFilter, CancellationToken cancellationToken);
  Task<int> RetryZeroSamplesAsync(double shrinkDays, CancellationToken cancellationToken);
}