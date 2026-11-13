using System.Reflection;

namespace Amurka.Shared.Core.Constants;

public static class AppConstants
{
    public const string ServerAddress = "85.88.179.207:7004";
    public const string ServerHost = "85.88.179.207";
    public const int ServerVerifyPort = 8080;
    public const string DiscordClientId = "1481533187732668476";
    public const string AppName = "AMURKA PVE Launcher";

    public static string Version { get; } = ResolveEntryVersion();

    private static string ResolveEntryVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "3.0.0";
    }
}
