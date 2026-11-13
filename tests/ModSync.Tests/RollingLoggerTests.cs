using Amurka.Shared.Logging;
using Microsoft.Extensions.Logging;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class RollingLoggerTests
{
    [Fact]
    public void RollingFileLoggerRotatesArchivesAndBoundsFileCount()
    {
        using var temp = new TempTestDir();
        string logFile = Path.Combine(temp.RootPath, "logs", "launcher.log");

        using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions
        {
            LogFilePath = logFile,
            MaxFileSizeBytes = 256,
            MaxArchiveFiles = 2,
            MinimumLevel = LogLevel.Information
        }))
        {
            ILogger logger = provider.CreateLogger("AmurkaTest");
            for (int i = 0; i < 25; i++)
            {
                logger.LogInformation("Enterprise diagnostic log line #{Iteration} with fixed padding", i);
            }
        }

        string logDir = Path.GetDirectoryName(logFile)!;
        Assert.True(File.Exists(logFile));
        Assert.True(File.Exists(Path.Combine(logDir, "launcher.1.log")));
        Assert.False(File.Exists(Path.Combine(logDir, "launcher.3.log")));
    }

    [Fact]
    public void CrashLoggerWritesStructuredExceptionEntry()
    {
        using var temp = new TempTestDir();
        string crashPath = Path.Combine(temp.RootPath, "crash.log");

        var ex = new InvalidOperationException("Simulated critical failure");
        CrashLogger.Log(crashPath, "UnitTestSource", ex);

        Assert.True(File.Exists(crashPath));
        string text = File.ReadAllText(crashPath);
        Assert.Contains("UnitTestSource", text, StringComparison.Ordinal);
        Assert.Contains("Simulated critical failure", text, StringComparison.Ordinal);
    }
}
