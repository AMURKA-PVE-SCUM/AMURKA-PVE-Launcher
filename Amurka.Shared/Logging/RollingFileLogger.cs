using Microsoft.Extensions.Logging;

namespace Amurka.Shared.Logging;

internal sealed class RollingFileLogger(string categoryName, RollingFileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        ArgumentNullException.ThrowIfNull(formatter);
        string message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        provider.WriteEntry(logLevel, categoryName, message, exception);
    }
}
