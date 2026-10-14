using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AmurkaLauncher.Services;

public partial class LauncherVerificationService
{
    private static readonly string[] DefaultEndpoints =
    [
        "http://127.0.0.1:8080/api/launcher/verify",
        "http://localhost:8080/api/launcher/verify",
        "http://85.88.179.207:8080/api/launcher/verify"
    ];

    private const ulong Steam64Base = 76561197960265728UL;
    private const long MaxLogFileSizeBytes = 1024 * 1024; // 1 MB limit to prevent disk leak

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "amurka-launcher",
        "verification.log"
    );

    [GeneratedRegex(@"""(7656119\d{10})""\s*\{([^}]+)\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SteamUserBlockRegex();

    [GeneratedRegex(@"""MostRecent""\s+""1""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MostRecentRegex();

    [GeneratedRegex(@"""AutoLogin""\s+""1""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoLoginRegex();

    [GeneratedRegex(@"""Timestamp""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"""(7656119\d{10})""", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackSteamIdRegex();

    private void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Ротация лога для защиты от бесконечной утечки дискового пространства
            var fileInfo = new FileInfo(LogFilePath);
            if (fileInfo.Exists && fileInfo.Length > MaxLogFileSizeBytes)
            {
                string oldLog = LogFilePath + ".old";
                File.Delete(oldLog);
                File.Move(LogFilePath, oldLog);
            }

            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, entry);
            Debug.WriteLine($"[LauncherVerify] {message}");
        }
        catch { }
    }

    /// <summary>
    /// Автоматическое определение активного SteamID64 пользователя на текущем ПК.
    /// 1. Сначала из активного процесса Steam в реестре (ActiveUser).
    /// 2. Если недоступно — из файла loginusers.vdf (пользователь с MostRecent/AutoLogin или наибольшим Timestamp).
    /// </summary>
    public string? GetActiveSteamId64(string? scumOrSteamPath = null)
    {
        // 1. Попытка чтения из активного процесса Steam в реестре
        try
        {
            using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (activeKey != null)
            {
                object? activeUserObj = activeKey.GetValue("ActiveUser");
                if (activeUserObj != null && long.TryParse(activeUserObj.ToString(), out long activeUserId) && activeUserId > 0)
                {
                    ulong steamId64 = Steam64Base + (ulong)activeUserId;
                    string res = steamId64.ToString();
                    Log($"SteamID detected from Registry ActiveProcess: {res}");
                    return res;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to read ActiveProcess registry: {ex.Message}");
        }

        // 2. Попытка чтения из loginusers.vdf
        try
        {
            string? steamDir = FindSteamPath(scumOrSteamPath);
            if (!string.IsNullOrEmpty(steamDir))
            {
                string vdfPath = Path.Combine(steamDir, "config", "loginusers.vdf");
                if (File.Exists(vdfPath))
                {
                    string content = File.ReadAllText(vdfPath);

                    var blockMatches = SteamUserBlockRegex().Matches(content);
                    string? bestSteamId = null;
                    long highestTimestamp = -1;

                    foreach (Match m in blockMatches)
                    {
                        string steamId = m.Groups[1].Value;
                        string blockBody = m.Groups[2].Value;

                        // Явный признак активного пользователя
                        if (MostRecentRegex().IsMatch(blockBody) || AutoLoginRegex().IsMatch(blockBody))
                        {
                            Log($"SteamID detected from loginusers.vdf (MostRecent/AutoLogin): {steamId}");
                            return steamId;
                        }

                        // Сравнение по Timestamp активности
                        var tsMatch = TimestampRegex().Match(blockBody);
                        if (tsMatch.Success && long.TryParse(tsMatch.Groups[1].Value, out long ts))
                        {
                            if (ts > highestTimestamp)
                            {
                                highestTimestamp = ts;
                                bestSteamId = steamId;
                            }
                        }
                        else if (bestSteamId == null)
                        {
                            bestSteamId = steamId;
                        }
                    }

                    if (!string.IsNullOrEmpty(bestSteamId))
                    {
                        Log($"SteamID detected from loginusers.vdf (by highest Timestamp/recent): {bestSteamId}");
                        return bestSteamId;
                    }

                    // Если структура нестандартная, берем первый валидный SteamID64
                    var firstMatch = FallbackSteamIdRegex().Match(content);
                    if (firstMatch.Success)
                    {
                        string steamId = firstMatch.Groups[1].Value;
                        Log($"SteamID detected from loginusers.vdf (fallback first match): {steamId}");
                        return steamId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to parse loginusers.vdf: {ex.Message}");
        }

        Log("Warning: Could not detect SteamID from registry or vdf");
        return null;
    }

    /// <summary>
    /// Отправка пинга верификации сразу на все возможные эндпоинты (локальный сервер и боевой сервер).
    /// </summary>
    public async Task<bool> SendVerificationToAllAsync(string steamId, string version, int installedModsCount, string? customServerIp = null)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            Log("Aborted: steamId is empty");
            return false;
        }

        var targets = new List<string>(DefaultEndpoints);

        // Если передан кастомный IP/хост сервера (например "85.88.179.207:7004"), извлекаем IP и порт 8080
        if (!string.IsNullOrWhiteSpace(customServerIp))
        {
            string host = customServerIp.Split(':')[0].Trim();
            if (!string.IsNullOrEmpty(host))
            {
                string customEndpoint = $"http://{host}:8080/api/launcher/verify";
                if (!targets.Contains(customEndpoint))
                {
                    targets.Add(customEndpoint);
                }
            }
        }

        var payload = new
        {
            steamId = steamId.Trim(),
            version = string.IsNullOrWhiteSpace(version) ? "2.1.21" : version.Trim(),
            installedCount = installedModsCount,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        byte[] jsonUtf8Bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        Log($"Sending verification for SteamID {steamId} (v{payload.version}, {installedModsCount} mods) to {targets.Count} endpoints...");

        bool anySuccess = false;
        var tasks = targets.Select(async url =>
        {
            try
            {
                using var content = new ByteArrayContent(jsonUtf8Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var response = await HttpClient.PostAsync(url, content);
                string respBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    Log($"Success from {url} (HTTP {(int)response.StatusCode}): {respBody}");
                    anySuccess = true;
                }
                else
                {
                    Log($"Non-success from {url} (HTTP {(int)response.StatusCode}): {respBody}");
                }
            }
            catch (Exception ex)
            {
                Log($"Endpoint {url} error: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        Log($"Verification finished. Success: {anySuccess}");
        return anySuccess;
    }

    private static string? FindSteamPath(string? hintPath)
    {
        // 1. Попытка извлечь путь к Steam из пути к SCUM (например D:\SteamLibrary\steamapps\common\SCUM)
        try
        {
            if (!string.IsNullOrWhiteSpace(hintPath))
            {
                string norm = hintPath.Replace('/', '\\');
                int idx = norm.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    string libRoot = norm[..idx];
                    string vdf = Path.Combine(libRoot, "config", "loginusers.vdf");
                    if (File.Exists(vdf)) return libRoot;
                }
            }
        }
        catch { }

        // 2. Реестр
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");

            if (key != null)
            {
                object? val = key.GetValue("SteamPath") ?? key.GetValue("InstallPath");
                if (val != null && Directory.Exists(val.ToString()))
                {
                    return val.ToString();
                }
            }
        }
        catch { }

        // 3. Стандартные папки
        string[] standard =
        [
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\Games\Steam",
            @"E:\Steam",
            @"E:\Games\Steam"
        ];

        foreach (var p in standard)
        {
            if (Directory.Exists(p)) return p;
        }

        return null;
    }
}
