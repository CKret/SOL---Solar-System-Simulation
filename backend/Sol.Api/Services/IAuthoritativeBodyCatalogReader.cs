using Sol.Api.Models;

namespace Sol.Api.Services;

public interface IAuthoritativeBodyCatalogReader
{
  Task<IReadOnlyList<CatalogBodySeed>> ReadBodiesAsync(CancellationToken cancellationToken);
}