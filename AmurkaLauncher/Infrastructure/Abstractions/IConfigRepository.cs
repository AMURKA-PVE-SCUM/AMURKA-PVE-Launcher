using AmurkaLauncher.Models;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IConfigRepository
{
    LauncherConfig LoadConfig();
    void SaveConfig(LauncherConfig config);
}
