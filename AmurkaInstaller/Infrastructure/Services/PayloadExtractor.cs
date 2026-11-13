using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Amurka.Shared.Core.Domain.Results;
using Amurka.Shared.IO;
using AmurkaInstaller.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaInstaller.Infrastructure.Services;

public sealed class PayloadExtractor(ILogger<PayloadExtractor>? logger = null) : IPayloadExtractor
{
    private const long MinimumRequiredBytes = 120L * 1024 * 1024; // 120 МБ запас

    public Result RunPreFlightChecks(string targetDir, long requiredBytes = 0)
    {
        Result<string> pathResult = PathSecurityGuard.ValidateInstallDirectory(targetDir);
        if (pathResult.IsFailure || string.IsNullOrWhiteSpace(pathResult.Value))
            return Result.Failure(pathResult.Error ?? "Укажите корректную папку установки.");

        string normalizedDir = pathResult.Value;

        try
        {
            string? root = Path.GetPathRoot(normalizedDir);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long needed = Math.Max(requiredBytes, MinimumRequiredBytes);
                    if (drive.AvailableFreeSpace < needed)
                    {
                        double needMb = needed / (1024.0 * 1024.0);
                        double freeMb = drive.AvailableFreeSpace / (1024.0 * 1024.0);
                        return Result.Failure(
                            $"Недостаточно свободного места на диске {drive.Name}. " +
                            $"Требуется минимум {needMb:F0} МБ, доступно {freeMb:F0} МБ.");
                    }
                }
            }

            Directory.CreateDirectory(normalizedDir);
            string probeFile = Path.Combine(normalizedDir, $".amurka_write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);

            return Result.Success();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            logger?.LogError(ex, "Pre-flight проверка каталога '{TargetDir}' не пройдена.", normalizedDir);
            return Result.Failure($"Нет прав на запись в каталог '{normalizedDir}': {ex.Message}");
        }
    }

    public async Task<Result> ExtractPayloadAsync(
        string targetDir,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct = default)
    {
        Result preflight = RunPreFlightChecks(targetDir);
        if (preflight.IsFailure)
            return preflight;

        return await Task.Run(() =>
        {
            Directory.CreateDirectory(targetDir);
            StreamTransferHelper.CleanupOldFiles(targetDir, logger);

            Assembly asm = Assembly.GetExecutingAssembly();
            string? resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(resourceName))
            {
                using Stream? resStream = asm.GetManifestResourceStream(resourceName);
                if (resStream is not null)
                {
                    long estimatedBytes = Math.Max(MinimumRequiredBytes, resStream.Length * 2);
                    Result spaceCheck = RunPreFlightChecks(targetDir, estimatedBytes);
                    if (spaceCheck.IsFailure)
                        return spaceCheck;

                    return ExtractFromZipStream(resStream, targetDir, progress, ct);
                }
            }

            // Режим самораспаковки / Single-Binary: копирование исполняемого файла лаунчера
            Result selfCopy = CopySelfExecutable(targetDir, progress, ct);
            if (selfCopy.IsSuccess)
                return selfCopy;

            // Режим разработки / отладки: копирование рядом лежащих файлов лаунчера, если payload.zip не вшит
            string sourceDir = AppContext.BaseDirectory;
            return CopyFallbackFiles(sourceDir, targetDir, progress, ct);
        }, ct);
    }

    private Result ExtractFromZipStream(
        Stream resStream,
        string targetDir,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(StreamTransferHelper.DefaultBufferSize);
        try
        {
            using var archive = new ZipArchive(resStream, ZipArchiveMode.Read, leaveOpen: true);
            List<ZipArchiveEntry> fileEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .ToList();

            long totalBytes = Math.Max(1, fileEntries.Sum(e => e.Length));
            long processedBytes = 0;

            foreach (ZipArchiveEntry entry in fileEntries)
            {
                ct.ThrowIfCancellationRequested();

                // Безопасное разрешение пути с защитой от Zip Slip
                string? destPath = PathSecurityGuard.ResolveSafeChildPath(targetDir, entry.FullName);
                if (destPath is null)
                {
                    logger?.LogWarning("Обнаружен небезопасный путь в архиве (Zip Slip): '{EntryName}'.", entry.FullName);
                    return Result.Failure($"Обнаружен небезопасный путь в архиве: {entry.FullName}");
                }

                string? parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                StreamTransferHelper.CopyToFileWithRetry(
                    () => entry.Open(),
                    destPath,
                    expectedLength: entry.Length,
                    sharedBuffer: sharedBuffer,
                    logger: logger,
                    ct: ct);

                processedBytes += entry.Length;
                int percent = Math.Clamp((int)(processedBytes * 80 / totalBytes) + 10, 10, 90);
                progress.Report((percent, $"Распаковка: {entry.Name}"));
            }

            logger?.LogInformation("Распаковка {FileCount} файлов в '{TargetDir}' завершена успешно.", fileEntries.Count, targetDir);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Ошибка распаковки встроенного payload.zip в '{TargetDir}'.", targetDir);
            return Result.Failure($"Ошибка распаковки файлов: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sharedBuffer);
        }
    }

    private Result CopyFallbackFiles(
        string sourceDir,
        string targetDir,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(StreamTransferHelper.DefaultBufferSize);
        try
        {
            string? currentExeName = Path.GetFileName(Environment.ProcessPath);
            string[] allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    if (!string.IsNullOrEmpty(currentExeName) && name.Equals(currentExeName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".vshost.exe", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .ToArray();

            if (allFiles.Length == 0)
            {
                return Result.Failure("Не найден встроенный архив payload.zip или файлы лаунчера для установки.");
            }

            long totalBytes = Math.Max(1, allFiles.Sum(f => new FileInfo(f).Length));
            long processedBytes = 0;

            foreach (string srcFile in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                string relPath = Path.GetRelativePath(sourceDir, srcFile);
                string? destPath = PathSecurityGuard.ResolveSafeChildPath(targetDir, relPath);
                if (destPath is null)
                    continue;

                string? parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                long srcLen = new FileInfo(srcFile).Length;
                StreamTransferHelper.CopyToFileWithRetry(
                    () => File.OpenRead(srcFile),
                    destPath,
                    expectedLength: srcLen,
                    sharedBuffer: sharedBuffer,
                    logger: logger,
                    ct: ct);

                processedBytes += srcLen;
                int percent = Math.Clamp((int)(processedBytes * 80 / totalBytes) + 10, 10, 90);
                progress.Report((percent, $"Копирование: {Path.GetFileName(srcFile)}"));
            }

            logger?.LogInformation("Резервное копирование {FileCount} файлов в '{TargetDir}' завершено.", allFiles.Length, targetDir);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Ошибка копирования файлов установки из '{SourceDir}' в '{TargetDir}'.", sourceDir, targetDir);
            return Result.Failure($"Ошибка копирования файлов: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sharedBuffer);
        }
    }

    private Result CopySelfExecutable(
        string targetDir,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(StreamTransferHelper.DefaultBufferSize);
        try
        {
            ct.ThrowIfCancellationRequested();
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                currentExe = current.MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                return Result.Failure("Не удалось определить путь к текущему исполняемому файлу.");
            }

            string fullCurrent = Path.GetFullPath(currentExe);
            string exeName = Path.GetFileName(fullCurrent);

            // Если запущен тест или утилита .NET CLI, не используем самокопирование
            if (exeName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                exeName.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
                exeName.Contains("vstest", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("Тестовое окружение: переход к резервному копированию.");
            }

            string targetExe = Path.Combine(targetDir, "AMURKA PVE Launcher.exe");
            string fullTarget = Path.GetFullPath(targetExe);

            if (string.Equals(fullCurrent, fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                progress.Report((90, "Файлы лаунчера актуальны."));
                return Result.Success();
            }

            progress.Report((20, "Копирование исполняемого файла лаунчера..."));
            Directory.CreateDirectory(targetDir);

            long fileLen = new FileInfo(fullCurrent).Length;
            StreamTransferHelper.CopyToFileWithRetry(
                () => File.OpenRead(fullCurrent),
                fullTarget,
                expectedLength: fileLen,
                sharedBuffer: sharedBuffer,
                logger: logger,
                ct: ct);

            progress.Report((90, "Установка файлов завершена."));
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Самокопирование не удалось, переключение на резервное копирование каталога.");
            return Result.Failure(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sharedBuffer);
        }
    }
}
