using DiscordRPC;

namespace AmurkaLauncher.Services;

public class DiscordRpcService : IDisposable
{
    private const string ClientId = "1481533187732668476";
    private DiscordRpcClient? _client;

    public void Initialize()
    {
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
        try
        {
            _client?.Deinitialize();
            _client?.Dispose();
            _client = null;
        }
        catch
        {
            // Ignore
        }
    }
}
