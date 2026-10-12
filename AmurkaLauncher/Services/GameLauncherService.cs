using System.Diagnostics;
using System.IO;

namespace AmurkaLauncher.Services;

public class GameLauncherService
{
    public void LaunchGame(string exePath, string mode)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new FileNotFoundException("Файл SCUM.exe не найден", exePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            UseShellExecute = true
        };

        // Стандартные аргументы запуска
        string[] baseArgs = ["-nobattleye", "-skipfilestreamingchecks", "-nohmd", "-nosplash", "-fileopenlog"];
        foreach (var arg in baseArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (string.Equals(mode, "dx12", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-dx12");
        }
        else if (string.Equals(mode, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-vulkan");
        }

        Process.Start(startInfo);
    }
}
