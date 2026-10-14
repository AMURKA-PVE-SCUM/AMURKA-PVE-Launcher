using DiscordRPC;

namespace AmurkaLauncher.Services;

public sealed class DiscordRpcService : IDisposable
{
    private const string ClientId = "1481533187732668476";
    private DiscordRpcClient? _client;
    private bool _disposed;

    public void Initialize()
    {
        if (_disposed) return;

        try
        {
            _client = new DiscordRpcClient(ClientId);
            _client.Initialize();
            _client.SetPresence(new RichPresence
            {
                Details = "AMURKA PVE",
                State = "85.88.179.207:7004",
                Timestamps = Timestamps.Now,
                Assets = new Assets
                {
                    LargeImageKey = "AMURKA",
                    LargeImageText = "85.88.179.207:7004"
                }
            });
        }
        catch
        {
            // Discord may not be running, ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _client?.Deinitialize();
            _client?.Dispose();
        }
        catch
        {
            // Ignore
        }
        finally
        {
            _client = null;
        }

        GC.SuppressFinalize(this);
    }
}
