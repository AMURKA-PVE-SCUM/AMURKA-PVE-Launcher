namespace AmurkaLauncher.Models;

public class ModItem
{
    public string Name { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
}
