using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Amurka.Shared.Security;

/// <summary>
/// Глобальный вычислитель хешей по спецификации Git Object Model (blob {size}\0{content}).
/// Обеспечивает высокопроизводительный потоковый расчет хешей без лишних аллокаций в памяти (Zero-Allocation).
/// </summary>
public static class GitBlobHasher
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Асинхронно вычисляет Git blob SHA-1 хеш файла с использованием пула буферов.
    /// </summary>
    public static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        AppendBlobHeader(hash, stream.Length);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, DefaultBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, count);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Синхронно вычисляет Git blob SHA-1 хеш массива байт в памяти (Zero-Allocation).
    /// </summary>
    public static string ComputeBytesHash(ReadOnlySpan<byte> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        AppendBlobHeader(hash, content.Length);
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Git blob SHA-1 format specification")]
    private static void AppendBlobHeader(IncrementalHash hash, long streamLength)
    {
        Span<byte> header = stackalloc byte[64];
        "blob "u8.CopyTo(header);
        int written = 5;
        if (Utf8Formatter.TryFormat(streamLength, header[written..], out int bytesWritten))
        {
            written += bytesWritten;
            header[written++] = 0;
            hash.AppendData(header[..written]);
        }
        else
        {
            hash.AppendData(Encoding.ASCII.GetBytes($"blob {streamLength}\0"));
        }
    }
}
