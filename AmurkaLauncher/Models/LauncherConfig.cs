using System.Text.Json.Serialization;

namespace AmurkaLauncher.Models;

public class LauncherConfig
{
    [JsonPropertyName("gamePath")]
    public string GamePath { get; set; } = string.Empty;

    [JsonPropertyName("modsPath")]
    public string ModsPath { get; set; } = string.Empty;

    [JsonPropertyName("launchMode")]
    public string LaunchMode { get; set; } = "dx11";

    [JsonPropertyName("categories")]
    public Dictionary<string, bool> Categories { get; set; } = new();
}
