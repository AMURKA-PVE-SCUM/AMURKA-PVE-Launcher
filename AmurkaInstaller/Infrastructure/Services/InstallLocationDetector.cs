using System.IO;
using AmurkaInstaller.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaInstaller.Infrastructure.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class InstallLocationDetector(
    IInstallerRegistryService registryService,
    ILogger<InstallLocationDetector>? logger = null) : IInstallLocationDetector
{
    public string DetermineDefaultInstallPath(bool isUpdate)
    {
        // 1. Проверяем реестр
        string? existing = registryService.GetExistingInstallLocation();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            if (Directory.Exists(existing) || isUpdate)
            {
                return existing;
            }
        }

        // 2. Для первой чистой установки: безопасный диск с играми (D:\, E:\, G:\, F:\)
        string[] candidateLetters = ["D:\\", "E:\\", "G:\\", "F:\\"];
        foreach (string letter in candidateLetters)
        {
            try
            {
                if (Directory.Exists(letter))
                {
                    var drive = new DriveInfo(letter);
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed && drive.AvailableFreeSpace > 1024L * 1024 * 1024)
                    {
                        return Path.Combine(drive.RootDirectory.FullName, "Games", "AMURKA PVE Launcher");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "Не удалось проверить диск '{DriveLetter}' при выборе пути установки.", letter);
            }
        }

        // 3. Каталог пользователя
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "AMURKA PVE Launcher");
    }

    public string GetDiskSpaceInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "Требуется свободного места: ~180 МБ";

            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var drive = new DriveInfo(root);
                double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                return $"Диск {drive.Name.TrimEnd('\\')}: свободно {freeGb:F1} ГБ · Требуется для установки: ~180 МБ";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger?.LogDebug(ex, "Не удалось определить свободное место для пути '{Path}'.", path);
        }

        return "Требуется свободного места: ~180 МБ";
    }
}
