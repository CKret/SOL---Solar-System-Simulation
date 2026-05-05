namespace Sol.Api.Services;

public interface IBodyCatalogImporter
{
  Task<BodyCatalogImportResult> ImportAsync(CancellationToken cancellationToken);
}

public sealed record BodyCatalogImportResult(int Inserted, int Updated, int Total);