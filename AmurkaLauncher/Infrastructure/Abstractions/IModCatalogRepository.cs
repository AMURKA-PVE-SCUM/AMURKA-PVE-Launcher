using AmurkaLauncher.Models;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IModCatalogRepository
{
    List<ModCategory> GetDefaultCategories();
    Task<List<ModItem>> FetchAvailableModsAsync(IEnumerable<ModCategory>? categories = null, CancellationToken ct = default);
}
