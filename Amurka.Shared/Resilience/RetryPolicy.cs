using Microsoft.Extensions.Logging;

namespace Amurka.Shared.Resilience;

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        ILogger? logger = null,
        string? operationName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        int attempts = Math.Max(1, maxAttempts);
        TimeSpan initialDelay = baseDelay ?? TimeSpan.FromMilliseconds(400);

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts && (shouldRetry == null || shouldRetry(ex)))
            {
                int backoffMs = (int)Math.Min(15_000, initialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                int jitterMs = Random.Shared.Next(40, 220);
                TimeSpan delay = TimeSpan.FromMilliseconds(backoffMs + jitterMs);

                logger?.LogWarning(
                    ex,
                    "Транзиентный сбой при выполнении '{Operation}' (попытка {Attempt}/{MaxAttempts}). Повтор через {DelayMs} мс.",
                    operationName ?? "operation",
                    attempt,
                    attempts,
                    (int)delay.TotalMilliseconds);

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    public static void ExecuteSync(
        Action operation,
        int maxAttempts = 5,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        ILogger? logger = null,
        string? operationName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        int attempts = Math.Max(1, maxAttempts);
        TimeSpan initialDelay = baseDelay ?? TimeSpan.FromMilliseconds(250);

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                operation();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts && (shouldRetry == null || shouldRetry(ex)))
            {
                int backoffMs = (int)Math.Min(5_000, initialDelay.TotalMilliseconds * Math.Pow(1.5, attempt - 1));
                int jitterMs = Random.Shared.Next(20, 120);
                int totalWait = backoffMs + jitterMs;

                logger?.LogDebug(
                    ex,
                    "Повтор операции '{Operation}' (попытка {Attempt}/{MaxAttempts}) через {DelayMs} мс.",
                    operationName ?? "sync-operation",
                    attempt,
                    attempts,
                    totalWait);

                if (ct.WaitHandle.WaitOne(totalWait))
                {
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
