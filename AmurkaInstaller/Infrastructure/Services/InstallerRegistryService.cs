using System.IO;
using AmurkaInstaller.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AmurkaInstaller.Infrastructure.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class InstallerRegistryService(ILogger<InstallerRegistryService>? logger = null) : IInstallerRegistryService
{
    private const string LauncherKey = @"Software\AMURKA PVE Launcher";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher";

    public string? GetExistingInstallLocation()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LauncherKey);
            if (key?.GetValue("InstallLocation") is string regPath && !string.IsNullOrWhiteSpace(regPath))
            {
                return regPath;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger?.LogDebug(ex, "Не удалось прочитать '{Key}' из реестра.", LauncherKey);
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string regPath && !string.IsNullOrWhiteSpace(regPath))
            {
                return regPath;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger?.LogDebug(ex, "Не удалось прочитать '{Key}' из реестра.", UninstallKey);
        }

        return null;
    }

    public void RegisterInstallation(string installDir, string version)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(LauncherKey))
            {
                key?.SetValue("InstallLocation", installDir);
            }

            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (key != null)
                {
                    string exePath = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
                    string uninstallerPath = Path.Combine(installDir, "uninstall.exe");

                    key.SetValue("DisplayName", $"AMURKA PVE Launcher v{version}");
                    key.SetValue("DisplayIcon", $"{exePath},0");
                    key.SetValue("DisplayVersion", version);
                    key.SetValue("Publisher", "AMURKA PVE");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("UninstallString", $"\"{uninstallerPath}\"");
                    key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /UNINSTALL");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }

            logger?.LogInformation("Запись установки зарегистрирована в реестре ({InstallDir}, v{Version}).", installDir, version);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Ошибка регистрации установки в реестре Windows.");
        }
    }

    public void UnregisterInstallation()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(LauncherKey, false);
            logger?.LogInformation("Записи установки удалены из реестра Windows.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger?.LogWarning(ex, "Ошибка удаления записей из реестра Windows.");
        }
    }
}
