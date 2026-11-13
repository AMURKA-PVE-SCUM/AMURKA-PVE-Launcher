using AmurkaLauncher.Models;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IUpdateService
{
    string CurrentVersion { get; }
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(UpdateInfo update, Action<int, long, long> onProgress, CancellationToken ct = default);
    void InstallUpdate(string installerPath);
}
