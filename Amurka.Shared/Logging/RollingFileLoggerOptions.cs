using Microsoft.Extensions.Logging;

namespace Amurka.Shared.Logging;

public sealed class RollingFileLoggerOptions
{
    public required string LogFilePath { get; init; }
    public long MaxFileSizeBytes { get; init; } = 5L * 1024 * 1024;
    public int MaxArchiveFiles { get; init; } = 3;
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
}
