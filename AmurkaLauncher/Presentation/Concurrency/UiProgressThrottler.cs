namespace AmurkaLauncher.Presentation.Concurrency;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// Обеспечивает сглаживание сетевого прогресса до стабильных 60 FPS в UI-потоке WPF (.NET 8 Channels).
/// Исключает зависание окна при гигабитной скорости скачивания и распаковки.
/// </summary>
public sealed class UiProgressThrottler<T>(Action<T> onBatchReady) : IAsyncDisposable
{
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = false,
        SingleReader = true
    });

    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerLoop;

    public ChannelWriter<T> Writer => _channel.Writer;

    public void Start(TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;

        _consumerLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16), timeProvider); // 60 FPS
            var reader = _channel.Reader;

            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    if (reader.TryRead(out var latestReport))
                    {
                        onBatchReady(latestReport);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination
            }
        }, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        if (_consumerLoop != null)
        {
            try
            {
                await _consumerLoop;
            }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
