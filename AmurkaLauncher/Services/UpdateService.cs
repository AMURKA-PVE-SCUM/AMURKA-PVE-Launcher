using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public partial class UpdateService
{
    private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubLatestRedirectUrl = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubRawLauncherJsonUrl = "https://raw.githubusercontent.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/main/dist/launcher.json";
    private const string GitHubReleasesDownloadBase = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/download";

    // Единственные экземпляры HttpClient для устранения утечки сокетов (Socket Exhaustion)
    private static readonly HttpClient HttpClient = CreateHttpClient(allowAutoRedirect: true, timeoutMinutes: 10);
    private static readonly HttpClient RedirectClient = CreateHttpClient(allowAutoRedirect: false, timeoutSeconds: 10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"/tag/(?:v|V)?([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagVersionRegex();

    private static HttpClient CreateHttpClient(bool allowAutoRedirect, int timeoutMinutes = 0, int timeoutSeconds = 0)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeoutMinutes > 0 ? TimeSpan.FromMinutes(timeoutMinutes) : TimeSpan.FromSeconds(timeoutSeconds)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AMURKA-Launcher/2.0");
        return client;
    }

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.1.21";
        }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        // 1) Способ №1: Официальный GitHub Releases API
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                if (root.TryGetProperty("tag_name", out var tagElem))
                {
                    string tagName = tagElem.GetString() ?? string.Empty;
                    string nextVer = Normalize(tagName);

                    if (IsNewerVersion(nextVer, CurrentVersion))
                    {
                        string downloadUrl = string.Empty;
                        string fileName = $"AMURKA-PVE-Launcher-Setup-{nextVer}.exe";
                        long size = 0;

                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    fileName = name;
                                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
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

        // 2) Способ №2: Редирект GitHub Releases (без API rate-limit и без утечки сокетов)
        try
        {
            using var resp = await RedirectClient.GetAsync(GitHubLatestRedirectUrl, HttpCompletionOption.ResponseHeadersRead);
            if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently)
            {
                var location = resp.Headers.Location;
                if (location != null)
                {
                    string locStr = location.ToString();
                    var match = TagVersionRegex().Match(locStr);
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
            using var response = await HttpClient.GetAsync(GitHubRawLauncherJsonUrl, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                var info = await JsonSerializer.DeserializeAsync<UpdateInfo>(stream, JsonOptions);

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
        string updatesFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "amurka-launcher",
            "updates"
        );
        Directory.CreateDirectory(updatesFolder);
        string destPath = Path.Combine(updatesFolder, updateInfo.File);

        using var response = await HttpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? updateInfo.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        // Используем пул массивов для устранения выделения LOH/heap буферов
        const int bufferSize = 81920;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long totalDownloaded = 0;
        int lastReportedPercent = -1;

        try
        {
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalDownloaded += bytesRead;
                if (totalBytes > 0)
                {
                    int pct = (int)Math.Min(100, Math.Round((double)totalDownloaded / totalBytes * 100));
                    if (pct != lastReportedPercent)
                    {
                        lastReportedPercent = pct;
                        onProgress(pct);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
                UseShellExecute = true
            });
        }
    }

    public static bool IsNewerVersion(string next, string current)
    {
        string normNext = Normalize(next);
        string normCurr = Normalize(current);

        if (Version.TryParse(normNext, out var vNext) && Version.TryParse(normCurr, out var vCurr))
        {
            return vNext > vCurr;
        }

        // Fallback без аллокаций через ReadOnlySpan<char>
        ReadOnlySpan<char> sNext = normNext.AsSpan();
        ReadOnlySpan<char> sCurr = normCurr.AsSpan();

        while (!sNext.IsEmpty || !sCurr.IsEmpty)
        {
            int nextVal = ParseNextVersionSegment(ref sNext);
            int currVal = ParseNextVersionSegment(ref sCurr);

            if (nextVal > currVal) return true;
            if (nextVal < currVal) return false;
        }

        return false;
    }

    private static int ParseNextVersionSegment(ref ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return 0;

        int dotIdx = span.IndexOf('.');
        ReadOnlySpan<char> segment;
        if (dotIdx >= 0)
        {
            segment = span[..dotIdx];
            span = span[(dotIdx + 1)..];
        }
        else
        {
            segment = span;
            span = [];
        }

        return int.TryParse(segment, out int val) ? val : 0;
    }

    private static string Normalize(string v)
    {
        return v.Trim().TrimStart('v', 'V');
    }
}
