using System.IO;
using Amurka.Shared.Core.Domain.Results;
using AmurkaLauncher.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Infrastructure.Services.Maintenance;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ClientMaintenanceService(
    IProcessSupervisor processSupervisor,
    ILogger<ClientMaintenanceService>? logger = null) : IClientMaintenanceService
{
    public async Task<Result<(bool wasEmpty, int deletedFiles)>> WipeClientCacheAsync(CancellationToken ct = default)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string scumDir = Path.Combine(localAppData, "SCUM");

        if (!Directory.Exists(scumDir))
        {
            return Result<(bool wasEmpty, int deletedFiles)>.Success((wasEmpty: true, deletedFiles: 0));
        }

        // 1. Завершаем активные процессы SCUM
        processSupervisor.KillGameProcesses();

        await Task.Delay(300, ct);

        // 2. Асинхронно очищаем каталог в пуле потоков
        return await Task.Run(() =>
        {
            try
            {
                var dirInfo = new DirectoryInfo(scumDir);
                FileInfo[] allFiles = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                if (allFiles.Length == 0 && dirInfo.GetDirectories().Length == 0)
                {
                    return Result<(bool wasEmpty, int deletedFiles)>.Success((wasEmpty: true, deletedFiles: 0));
                }

                int deletedCount = 0;
                foreach (FileInfo file in allFiles)
                {
                    try
                    {
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                        deletedCount++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger?.LogWarning(ex, "Не удалось удалить файл кэша '{FilePath}'.", file.FullName);
                    }
                }

                try
                {
                    dirInfo.Delete(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "Не удалось полностью удалить папку кэша '{CacheDir}'.", scumDir);
                }

                if (Directory.Exists(scumDir))
                {
                    return Result<(bool wasEmpty, int deletedFiles)>.Failure("Часть файлов осталась заблокирована операционной системой.");
                }

                logger?.LogInformation("Кэш клиента SCUM очищен: удалено файлов = {DeletedCount}.", deletedCount);
                return Result<(bool wasEmpty, int deletedFiles)>.Success((wasEmpty: false, deletedFiles: deletedCount));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Ошибка очистки кэша SCUM в '{CacheDir}'.", scumDir);
                return Result<(bool wasEmpty, int deletedFiles)>.Failure(ex.Message);
            }
        }, ct);
    }
}
