using System.IO;
using System.Text.Json;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public class ConfigService
{
    private static readonly string PrimaryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "amurka-launcher",
        "config.json"
    );

    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".amurka",
        "config.json"
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LauncherConfig LoadConfig()
    {
        string[] paths = [PrimaryPath, BackupPath];
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    string json = File.ReadAllText(p);
                    var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch
            {
                // Ignore and try next path
            }
        }

        return new LauncherConfig();
    }

    public void SaveConfig(LauncherConfig config)
    {
        string[] paths = [PrimaryPath, BackupPath];
        string json = JsonSerializer.Serialize(config, JsonOptions);

        foreach (var p in paths)
        {
            try
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(p, json);
            }
            catch
            {
                // Best effort saving
            }
        }
    }
}
