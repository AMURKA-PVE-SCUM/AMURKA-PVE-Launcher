using Amurka.Shared.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IModSyncEngine
{
    Task RefreshInstalledStateAsync(IEnumerable<ModItem> mods, ModsPath modsPath, CancellationToken ct = default);
    Task<(bool isSynced, List<ModItem> missingOrCorrupt, List<string> obsoleteFiles)> CheckSynchronizationAsync(
        List<ModItem> catalog,
        ModsPath modsPath,
        CancellationToken ct = default);
    Task<Result<(int success, int errors)>> DownloadAllModsAsync(
        List<ModItem> mods,
        ModsPath modsPath,
        IProgress<SyncProgressReport> progress,
        CancellationToken ct = default);
    List<string> FindUntrackedOrObsoleteFiles(List<ModItem> catalog, ModsPath modsPath);
    Task<int> RemoveObsoleteAndUntrackedModsAsync(List<ModItem> catalog, ModsPath modsPath, CancellationToken ct = default);
    Task<List<ManagedMod>> FindObsoleteModsAsync(List<ModItem> catalog, ModsPath modsPath, CancellationToken ct = default);
    int DeleteAllMods(ModsPath modsPath);
}
