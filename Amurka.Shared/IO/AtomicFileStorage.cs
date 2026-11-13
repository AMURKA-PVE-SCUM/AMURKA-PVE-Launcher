using System.IO;
using System.Text;

namespace Amurka.Shared.IO;

public static class AtomicFileStorage
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllTextAtomic(string targetPath, string content, Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        byte[] bytes = (encoding ?? Utf8NoBom).GetBytes(content);
        WriteAllBytesAtomic(targetPath, bytes);
    }

    public static void WriteAllBytesAtomic(string targetPath, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string fullTarget = Path.GetFullPath(targetPath);
        string? dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{fullTarget}.tmp.{Guid.NewGuid():N}";
        bool moved = false;

        try
        {
            using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullTarget, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public static async Task WriteAllTextAtomicAsync(
        string targetPath,
        string content,
        Encoding? encoding = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        byte[] bytes = (encoding ?? Utf8NoBom).GetBytes(content);
        await WriteAllBytesAtomicAsync(targetPath, bytes, ct).ConfigureAwait(false);
    }

    public static async Task WriteAllBytesAtomicAsync(
        string targetPath,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string fullTarget = Path.GetFullPath(targetPath);
        string? dir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{fullTarget}.tmp.{Guid.NewGuid():N}";
        bool moved = false;

        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(data, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullTarget, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
