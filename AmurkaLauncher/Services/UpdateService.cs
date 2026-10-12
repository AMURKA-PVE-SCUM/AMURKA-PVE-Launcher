using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public class UpdateService
{
    private const string GitHubLauncherJsonUrl = "https://raw.githubusercontent.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/main/dist/launcher.json";
    private const string GitHubReleasesDownloadBase = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/download";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static UpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AMURKA-Launcher/2.0");
    }

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.1.17";
        }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            // 1) Проверка через GitHub
            using var response = await HttpClient.GetAsync(GitHubLauncherJsonUrl);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (info != null && !string.IsNullOrEmpty(info.Version) && !string.IsNullOrEmpty(info.File))
                {
                    if (IsNewerVersion(info.Version, CurrentVersion))
                    {
                        info.DownloadUrl = $"{GitHubReleasesDownloadBase}/v{info.Version}/{info.File}";
                        return info;
                    }
                    return null;
                }
            }
        }
        catch
        {
            // Fallback
        }

        return null;
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo updateInfo, Action<int> onProgress, CancellationToken ct = default)
    {
        string tempFolder = Path.GetTempPath();
        string destPath = Path.Combine(tempFolder, updateInfo.File);

        using var response = await HttpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? updateInfo.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        byte[] buffer = new byte[81920];
        long totalDownloaded = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalDownloaded += bytesRead;
            if (totalBytes > 0)
            {
                int pct = (int)Math.Min(100, Math.Round((double)totalDownloaded / totalBytes * 100));
                onProgress(pct);
            }
        }

        return destPath;
    }

    public void InstallUpdate(string installerPath)
    {
        if (File.Exists(installerPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/S",
                UseShellExecute = true
            });
        }
    }

    public static bool IsNewerVersion(string next, string current)
    {
        var a = Normalize(next).Split('.').Select(p => int.TryParse(p, out int v) ? v : 0).ToArray();
        var b = Normalize(current).Split('.').Select(p => int.TryParse(p, out int v) ? v : 0).ToArray();

        int max = Math.Max(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x > y) return true;
            if (x < y) return false;
        }

        return false;
    }

    private static string Normalize(string v)
    {
        return v.Trim().TrimStart('v', 'V');
    }
}
