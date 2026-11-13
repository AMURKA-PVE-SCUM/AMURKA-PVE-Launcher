using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Network;
using AmurkaLauncher.Core.Serialization;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Services;

public sealed partial class UpdateService(
    HttpClient? client = null,
    HttpClient? redirectClient = null,
    TimeProvider? timeProvider = null,
    ILogger<UpdateService>? logger = null) : IUpdateService
{
    private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubLatestRedirectUrl = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest";
    private const string GitHubRawLauncherJsonUrl = "https://raw.githubusercontent.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/main/dist/launcher.json";
    private const string GitHubReleasesDownloadBase = "https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/download";

    // Единственный экземпляр HttpClient во избежание утечки сокетов (Socket Exhaustion)
    private static readonly HttpClient SharedHttpClient = HttpClientDefaults.CreateClient(TimeSpan.FromMinutes(10), allowAutoRedirect: true);
    private static readonly HttpClient SharedRedirectClient = HttpClientDefaults.CreateClient(TimeSpan.FromSeconds(10), allowAutoRedirect: false);
    private readonly HttpClient HttpClient = client ?? SharedHttpClient;
    private readonly HttpClient RedirectClient = redirectClient ?? SharedRedirectClient;
    private readonly TimeProvider TimeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<UpdateService>? _logger = logger;

    public string? UpdatesDirectory { get; set; }
    public string? VersionOverride { get; set; }

    [GeneratedRegex(@"/tag/(?:v|V)?([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagVersionRegex();

    public string CurrentVersion => !string.IsNullOrEmpty(VersionOverride) ? VersionOverride : AppConstants.Version;

    public string GetUpdatesFolder()
    {
        return UpdatesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "amurka-launcher",
            "updates"
        );
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // 1) Static launcher.json: no unauthenticated GitHub API quota.
        try
        {
            using var response = await HttpClient.GetAsync(GitHubRawLauncherJsonUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var info = await JsonSerializer.DeserializeAsync(stream, LauncherJsonContext.Default.UpdateInfo, ct);

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
            _logger?.LogDebug(ex, "Проверка raw launcher.json не удалась.");
        }

        // 2) Fallback: редирект GitHub Releases (без API rate-limit и без утечки сокетов)
        try
        {
            using var resp = await RedirectClient.GetAsync(GitHubLatestRedirectUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
            _logger?.LogDebug(ex, "Проверка редиректа GitHub Releases не удалась.");
        }

        // 3) Last resort: GitHub Releases API.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
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
            _logger?.LogDebug(ex, "Проверка GitHub Releases API не удалась.");
        }

        return null;
    }

    public Task<string> DownloadUpdateAsync(UpdateInfo update, Action<int> onProgress, CancellationToken ct = default)
    {
        return DownloadUpdateAsync(update, (pct, _, _) => onProgress(pct), ct);
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, Action<int, long, long> onProgress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        string updatesFolder = GetUpdatesFolder();
        Directory.CreateDirectory(updatesFolder);
        string destPath = Path.Combine(updatesFolder, update.File);
        string tempPath = destPath + ".tmp";

        // Fast path: если файл уже скачан целиком, проверен по размеру и хэшу — возвращаем сразу
        if (File.Exists(destPath))
        {
            var existingFi = new FileInfo(destPath);
            if (update.Size <= 0 || existingFi.Length == update.Size)
            {
                if (string.IsNullOrWhiteSpace(update.Sha512) || VerifySha512(destPath, update.Sha512, update.Size))
                {
                    long total = existingFi.Length;
                    onProgress(100, total, total);
                    return destPath;
                }
            }
            TryDeleteFile(destPath);
        }

        const int maxRetries = 4;
        const int bufferSize = 81920;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                long existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                if (update.Size > 0 && existingLength > update.Size)
                {
                    TryDeleteFile(tempPath);
                    existingLength = 0;
                }
                else if (update.Size > 0 && existingLength == update.Size)
                {
                    if (string.IsNullOrWhiteSpace(update.Sha512) || VerifySha512(tempPath, update.Sha512, update.Size))
                    {
                        File.Move(tempPath, destPath, overwrite: true);
                        onProgress(100, update.Size, update.Size);
                        return destPath;
                    }
                    TryDeleteFile(tempPath);
                    existingLength = 0;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
                    if (existingLength > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(existingLength, null);
                    }

                    using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        TryDeleteFile(tempPath);
                        existingLength = 0;
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
                    long totalBytes;
                    if (isPartial)
                    {
                        totalBytes = response.Content.Headers.ContentRange?.Length ?? update.Size;
                    }
                    else
                    {
                        totalBytes = response.Content.Headers.ContentLength ?? update.Size;
                        existingLength = 0; // Начинаем заново, если сервер вернул 200 OK
                    }

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(
                        tempPath,
                        isPartial && existingLength > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize,
                        useAsync: true);

                    long totalDownloaded = existingLength;
                    int lastReportedPercent = -1;

                    if (totalBytes > 0 && existingLength > 0)
                    {
                        int initialPct = (int)Math.Min(100, Math.Round((double)totalDownloaded / totalBytes * 100));
                        lastReportedPercent = initialPct;
                        onProgress(initialPct, totalDownloaded, totalBytes);
                    }

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
                                onProgress(pct, totalDownloaded, totalBytes);
                            }
                        }
                    }

                    await fileStream.FlushAsync(ct);
                    fileStream.Close();

                    // Проверка целостности
                    var fi = new FileInfo(tempPath);
                    if (totalBytes > 0 && fi.Length != totalBytes)
                    {
                        throw new IOException($"Размер скачанного файла ({fi.Length}) не совпадает с ожидаемым ({totalBytes}).");
                    }

                    if (!string.IsNullOrWhiteSpace(update.Sha512) && !VerifySha512(tempPath, update.Sha512, update.Size > 0 ? update.Size : null))
                    {
                        TryDeleteFile(tempPath);
                        throw new IOException("Контрольная сумма SHA-512 скачанного обновления не совпадает.");
                    }

                    File.Move(tempPath, destPath, overwrite: true);
                    onProgress(100, totalDownloaded, totalBytes > 0 ? totalBytes : totalDownloaded);
                    return destPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger?.LogWarning(ex, "Попытка загрузки обновления #{Attempt} завершилась ошибкой. Повтор...", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(6, attempt * 2)), ct);
                }
            }

            throw new IOException($"Не удалось завершить загрузку обновления после {maxRetries} попыток.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Не удалось удалить временный файл обновления '{Path}'.", path);
        }
    }

    public static bool VerifySha512(string filePath, string? expectedHex, long? expectedSize = null)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            if (expectedSize is > 0 && new FileInfo(filePath).Length != expectedSize.Value)
                return false;

            using var sha = SHA512.Create();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            byte[] hash = sha.ComputeHash(fs);
            return string.Equals(Convert.ToHexString(hash), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    public void InstallUpdate(string installerPath)
    {
        if (File.Exists(installerPath))
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/UPDATE",
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

        // Fallback по сегментам через ReadOnlySpan<char>
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
