using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public class UpdateService
{
    private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubLatestRedirectUrl = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubRawLauncherJsonUrl = "https://raw.githubusercontent.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/main/launcher.json";
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
        // 1) Способ №1: Официальный GitHub Releases API
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
            using var response = await HttpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("tag_name", out var tagElem))
                {
                    string tagName = tagElem.GetString() ?? "";
                    string nextVer = Normalize(tagName);

                    if (IsNewerVersion(nextVer, CurrentVersion))
                    {
                        string downloadUrl = "";
                        string fileName = $"AMURKA-PVE-Launcher-Setup-{nextVer}.exe";
                        long size = 0;

                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    fileName = name;
                                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(downloadUrl))
                        {
                            downloadUrl = $"{GitHubReleasesDownloadBase}/v{nextVer}/{fileName}";
                        }

                        return new UpdateInfo
                        {
                            Version = nextVer,
                            File = fileName,
                            DownloadUrl = downloadUrl,
                            Size = size
                        };
                    }
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] GitHub API check failed: {ex.Message}");
        }

        // 2) Способ №2: Редирект GitHub Releases (без API rate-limit)
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var redirectClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            redirectClient.DefaultRequestHeaders.UserAgent.ParseAdd("AMURKA-Launcher/2.0");

            using var resp = await redirectClient.GetAsync(GitHubLatestRedirectUrl);
            if (resp.StatusCode == System.Net.HttpStatusCode.Redirect || resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
            {
                var location = resp.Headers.Location;
                if (location != null)
                {
                    string locStr = location.ToString();
                    var match = System.Text.RegularExpressions.Regex.Match(locStr, @"/tag/(?:v|V)?([0-9.]+)");
                    if (match.Success)
                    {
                        string nextVer = match.Groups[1].Value.Trim();
                        if (IsNewerVersion(nextVer, CurrentVersion))
                        {
                            string fileName = $"AMURKA-PVE-Launcher-Setup-{nextVer}.exe";
                            return new UpdateInfo
                            {
                                Version = nextVer,
                                File = fileName,
                                DownloadUrl = $"{GitHubReleasesDownloadBase}/v{nextVer}/{fileName}"
                            };
                        }
                        return null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] GitHub redirect check failed: {ex.Message}");
        }

        // 3) Способ №3: Резервный launcher.json из корня репозитория
        try
        {
            using var response = await HttpClient.GetAsync(GitHubRawLauncherJsonUrl);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (info != null && !string.IsNullOrEmpty(info.Version))
                {
                    if (IsNewerVersion(info.Version, CurrentVersion))
                    {
                        if (string.IsNullOrEmpty(info.File))
                        {
                            info.File = $"AMURKA-PVE-Launcher-Setup-{Normalize(info.Version)}.exe";
                        }
                        info.DownloadUrl = $"{GitHubReleasesDownloadBase}/v{Normalize(info.Version)}/{info.File}";
                        return info;
                    }
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] GitHub raw json check failed: {ex.Message}");
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
