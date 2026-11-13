using System.IO;
using System.Text.Json;
using AmurkaLauncher.Core.Serialization;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public sealed class ConfigService : IConfigRepository
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
                    var cfg = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherConfig);
                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Ignore and try next path
            }
        }

        return new LauncherConfig();
    }

    public void SaveConfig(LauncherConfig config)
    {
        string[] paths = [PrimaryPath, BackupPath];
        string json = JsonSerializer.Serialize(config, LauncherJsonContext.Default.LauncherConfig);

        foreach (var p in paths)
        {
            try
            {
                Amurka.Shared.IO.AtomicFileStorage.WriteAllTextAtomic(p, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort saving (backup path covers failure on primary or vice versa)
            }
        }
    }
}
