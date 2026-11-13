using System.Net.Http;
using Amurka.Shared.Core.Constants;

namespace Amurka.Shared.Network;

public static class HttpClientDefaults
{
    public static SocketsHttpHandler CreateSocketsHandler(bool allowAutoRedirect = true)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(14),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true
        };
    }

    public static HttpClient CreateClient(
        TimeSpan timeout,
        bool allowAutoRedirect = true,
        HttpMessageHandler? overrideHandler = null)
    {
        var handler = overrideHandler ?? CreateSocketsHandler(allowAutoRedirect);
        var client = new HttpClient(handler, disposeHandler: overrideHandler == null)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AMURKA-Launcher/{AppConstants.Version}");
        return client;
    }
}
