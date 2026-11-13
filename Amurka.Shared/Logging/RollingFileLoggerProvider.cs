using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Amurka.Shared.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private bool _disposed;

    public LogLevel MinimumLevel => _options.MinimumLevel;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LogFilePath))
            throw new ArgumentException("LogFilePath must not be empty.", nameof(options));

        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));
    }

    internal void WriteEntry(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed) return;

        string levelTag = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "LOG"
        };

        var sb = new StringBuilder(256);
        sb.Append('[');
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append("] [");
        sb.Append(levelTag);
        sb.Append("] [");
        sb.Append(category);
        sb.Append("] [T:");
        sb.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        sb.Append("] ");
        sb.Append(message);

        if (exception != null)
        {
            sb.Append(" | EX: ");
            sb.Append(exception);
        }

        sb.AppendLine();
        byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());

        lock (_writeLock)
        {
            if (_disposed) return;
            try
            {
                string? dir = Path.GetDirectoryName(_options.LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded(payload.Length);

                using var fs = new FileStream(
                    _options.LogFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096);
                fs.Write(payload);
            }
            catch
            {
                // Logging must never crash the host application
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_options.LogFilePath)) return;

        var info = new FileInfo(_options.LogFilePath);
        if (info.Length + incomingBytes <= _options.MaxFileSizeBytes) return;

        int maxArchives = Math.Max(1, _options.MaxArchiveFiles);
        string dir = Path.GetDirectoryName(_options.LogFilePath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(_options.LogFilePath);
        string ext = Path.GetExtension(_options.LogFilePath);

        string oldest = Path.Combine(dir, $"{baseName}.{maxArchives}{ext}");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = maxArchives - 1; i >= 1; i--)
        {
            string src = Path.Combine(dir, $"{baseName}.{i}{ext}");
            string dst = Path.Combine(dir, $"{baseName}.{i + 1}{ext}");
            if (File.Exists(src))
            {
                File.Move(src, dst, overwrite: true);
            }
        }

        string firstArchive = Path.Combine(dir, $"{baseName}.1{ext}");
        File.Move(_options.LogFilePath, firstArchive, overwrite: true);
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _loggers.Clear();
        }
    }
}
