using System.Text.Json.Serialization;

namespace AmurkaLauncher.Models;

public class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha512")]
    public string Sha512 { get; set; } = string.Empty;

    [JsonIgnore]
    public string DownloadUrl { get; set; } = string.Empty;
}
