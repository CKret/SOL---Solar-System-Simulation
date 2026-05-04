using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IEphemerisSampleImporter
{
  Task<EphemerisSampleImportResult> ImportAsync(double? hMax, DateTime? startUtc, DateTime? endUtc, TimeSpan? sampleRateOverride, CancellationToken cancellationToken);
}