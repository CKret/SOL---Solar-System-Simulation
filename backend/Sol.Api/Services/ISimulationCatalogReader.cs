using Sol.Api.Models;

namespace Sol.Api.Services;

public interface ISimulationCatalogReader
{
  IReadOnlyList<CatalogBodySeed> ReadBodies();
}