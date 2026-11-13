using System.Buffers;
using System.IO;
using Amurka.Shared.Resilience;
using Microsoft.Extensions.Logging;

namespace Amurka.Shared.IO;

public static class StreamTransferHelper
{
    public const int DefaultBufferSize = 81_920;

    public static void CopyToFileWithRetry(
        Func<Stream> sourceFactory,
        string destinationPath,
        long expectedLength = 0,
        byte[]? sharedBuffer = null,
        Action<int>? onBytesCopied = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        byte[] buffer = sharedBuffer ?? ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        bool returnBuffer = sharedBuffer == null;

        try
        {
            try
            {
                RetryPolicy.ExecuteSync(
                    () => CopyCore(sourceFactory, destinationPath, expectedLength, buffer, onBytesCopied, ct),
                    maxAttempts: 5,
                    baseDelay: TimeSpan.FromMilliseconds(250),
                    logger: logger,
                    operationName: Path.GetFileName(destinationPath),
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Файл '{Path}' заблокирован. Пробуем транзакционное переименование в .old.", destinationPath);
                TryMoveLockedFileToOld(destinationPath, logger);
                CopyCore(sourceFactory, destinationPath, expectedLength, buffer, onBytesCopied, ct);
            }
        }
        finally
        {
            if (returnBuffer)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void CopyCore(
        Func<Stream> sourceFactory,
        string destinationPath,
        long expectedLength,
        byte[] buffer,
        Action<int>? onBytesCopied,
        CancellationToken ct)
    {
        PrepareDestinationFile(destinationPath);

        using Stream src = sourceFactory();
        using var dest = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        if (expectedLength > 0)
        {
            dest.SetLength(expectedLength);
        }

        int bytesRead;
        while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dest.Write(buffer, 0, bytesRead);
            onBytesCopied?.Invoke(bytesRead);
        }

        dest.Flush();
    }

    public static void PrepareDestinationFile(string path)
    {
        if (!File.Exists(path)) return;

        try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        try { File.Delete(path); } catch { }
    }

    public static void TryMoveLockedFileToOld(string destinationPath, ILogger? logger = null)
    {
        if (!File.Exists(destinationPath)) return;

        try
        {
            File.SetAttributes(destinationPath, FileAttributes.Normal);
            string tempOld = $"{destinationPath}.old.{Guid.NewGuid():N}";
            File.Move(destinationPath, tempOld);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Не удалось переместить заблокированный файл '{Path}' во временное имя .old.", destinationPath);
        }
    }

    public static void CleanupOldFiles(string targetDirectory, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(targetDirectory)) return;

            foreach (string oldFile in Directory.EnumerateFiles(targetDirectory, "*.old.*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(oldFile);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Не удалось удалить временный файл '{OldFile}'.", oldFile);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ошибка очистки временных файлов *.old.* в каталоге '{Dir}'.", targetDirectory);
        }
    }
}
