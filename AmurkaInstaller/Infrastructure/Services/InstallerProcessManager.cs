using System.Diagnostics;
using System.IO;
using System.Text;
using Amurka.Shared.IO;
using Amurka.Shared.Platform;
using AmurkaInstaller.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaInstaller.Infrastructure.Services;

public sealed class InstallerProcessManager(ILogger<InstallerProcessManager>? logger = null) : IInstallerProcessManager
{
    private const string PrimaryProcessName = "AMURKA PVE Launcher";
    private static readonly string[] LauncherProcessNames = ["AMURKA PVE Launcher", "AMURKA-PVE-Launcher", "AmurkaLauncher"];

    public async Task WaitForLauncherExitAsync(TimeSpan timeout, string? installDir = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!ProcessManagerHelper.IsAnyProcessRunning(PrimaryProcessName))
                return;

            await Task.Delay(300, ct);
        }

        KillRunningLauncherProcesses(installDir);
    }

    public void KillRunningLauncherProcesses(string? installDir = null) =>
        ProcessManagerHelper.KillProcesses(LauncherProcessNames, excludeCurrentProcess: true, logger: logger);

    public void LaunchInstalledApp(string installDir)
    {
        try
        {
            string exe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Не удалось запустить установленный лаунчер из '{InstallDir}'.", installDir);
        }
    }

    public void ScheduleSelfDirectoryDeletion(string installDir)
    {
        var validated = PathSecurityGuard.ValidateInstallDirectory(installDir);
        if (validated.IsFailure || !Directory.Exists(validated.Value))
        {
            logger?.LogWarning("Пропущено самоудаление небезопасного или несуществующего каталога '{InstallDir}'.", installDir);
            return;
        }

        string safeDir = validated.Value;
        // Дополнительная защита от batch-инъекций через кавычки/управляющие символы
        if (safeDir.IndexOfAny(['"', '%', '&', '|', '^', '<', '>', '\r', '\n']) >= 0)
        {
            logger?.LogWarning("Путь '{SafeDir}' содержит недопустимые символы для batch-скрипта удаления.", safeDir);
            return;
        }

        int currentPid = Environment.ProcessId;
        string scriptPath = Path.Combine(Path.GetTempPath(), $"amurka-uninstall-{Guid.NewGuid():N}.bat");
        string batContent = $@"@echo off
chcp 65001 >nul
:wait_exit
tasklist /fi ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_exit
)
timeout /t 1 /nobreak >nul
if exist ""{safeDir}"" (
    rd /s /q ""{safeDir}""
)
del ""%~f0""
";
        try
        {
            File.WriteAllText(scriptPath, batContent, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Не удалось запланировать самоудаление каталога '{SafeDir}'.", safeDir);
        }
    }
}
