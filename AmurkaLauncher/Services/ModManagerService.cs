using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public class ModManagerService
{
    private const string GitHubRepoOwner = "AMURKA-PVE-SCUM";
    private const string GitHubRepoName = "amurka-pve-mods";
    private const string GitHubBranch = "main";
    private const int DownloadConcurrency = 4;
    private const int BufferSize = 81920;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AMURKA-Launcher/2.0");
        return client;
    }

    public List<ModCategory> GetDefaultCategories()
    {
        return
        [
            new ModCategory { Id = "core", Label = "Основные", Required = true, IsSelected = true },
            new ModCategory { Id = "speed", Label = "Скорость транспорта", Required = false, IsSelected = false },
            new ModCategory { Id = "skins", Label = "Скины одежды", Required = false, IsSelected = false },
            new ModCategory { Id = "weapons", Label = "Скины на оружие", Required = false, IsSelected = false },
            new ModCategory { Id = "buildings", Label = "Скины на постройки", Required = false, IsSelected = false },
            new ModCategory { Id = "music", Label = "Музыка", Required = false, IsSelected = false }
        ];
    }

    public async Task<List<ModItem>> FetchAvailableModsAsync(IEnumerable<ModCategory> categories)
    {
        var categorySet = categories.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModItem>();

        // 1) Быстрое получение дерева репозитория через GitHub Tree API за 1 запрос
        try
        {
            string treeUrl = $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/git/trees/{GitHubBranch}?recursive=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, treeUrl);
            using var response = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in tree.EnumerateArray())
                    {
                        string path = item.TryGetProperty("path", out var pProp) ? pProp.GetString() ?? string.Empty : string.Empty;
                        string type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                        long size = item.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;

                        bool isModFile = path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                                         path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);

                        if (type == "blob" && isModFile)
                        {
                            var parts = path.Split('/', 2);
                            if (parts.Length == 2 && categorySet.Contains(parts[0]))
                            {
                                string category = parts[0];
                                string fileName = parts[1];
                                string rawUrl = $"https://raw.githubusercontent.com/{GitHubRepoOwner}/{GitHubRepoName}/{GitHubBranch}/{Uri.EscapeDataString(category)}/{Uri.EscapeDataString(fileName)}";

                                result.Add(new ModItem
                                {
                                    Name = fileName,
                                    DownloadUrl = rawUrl,
                                    Size = size,
                                    Category = category,
                                    IsInstalled = false
                                });
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback to per-category
        }

        // 2) Fallback: опрос по категориям через GitHub Contents API
        if (result.Count == 0)
        {
            foreach (var cat in categories)
            {
                try
                {
                    string url = $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/contents/{cat.Id}";
                    using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode) continue;

                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                            string downloadUrl = item.TryGetProperty("download_url", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                            long size = item.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                            bool isModFile = name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                                             name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);

                            if (type == "file" && isModFile)
                            {
                                if (string.IsNullOrEmpty(downloadUrl))
                                {
                                    downloadUrl = $"https://raw.githubusercontent.com/{GitHubRepoOwner}/{GitHubRepoName}/{GitHubBranch}/{cat.Id}/{Uri.EscapeDataString(name)}";
                                }

                                result.Add(new ModItem
                                {
                                    Name = name,
                                    DownloadUrl = downloadUrl,
                                    Size = size,
                                    Category = cat.Id,
                                    IsInstalled = false
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("Не удалось получить список модов с GitHub");
        }

        return result;
    }

    public List<string> ScanInstalledMods(string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return [];
        }

        try
        {
            var list = new List<string>();
            foreach (var file in Directory.EnumerateFiles(modsPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                {
                    string? name = Path.GetFileName(file);
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(name);
                    }
                }
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    public async Task<(int success, int errors)> DownloadAllModsAsync(
        List<ModItem> mods,
        string modsPath,
        Action<int, string, long, long> onProgress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modsPath))
        {
            throw new InvalidOperationException("Папка модов не указана");
        }

        // Строгая проверка безопасности: установка разрешена исключительно в папку ~mods
        var dirInfo = new DirectoryInfo(modsPath);
        if (!dirInfo.Name.Equals("~mods", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Установка разрешена только в папку ~mods! Текущая папка: {modsPath}");
        }

        Directory.CreateDirectory(modsPath);

        // Определяем очередь модов: отсутствующие или недокачанные файлы
        var queue = mods.Where(m =>
        {
            string dest = Path.Combine(modsPath, m.Name);
            if (!File.Exists(dest)) return true;
            if (m.Size > 0 && new FileInfo(dest).Length != m.Size) return true;
            return false;
        }).ToList();

        long totalBytes = queue.Sum(m => m.Size);
        int success = mods.Count - queue.Count;
        int errors = 0;

        if (queue.Count == 0)
        {
            onProgress(100, "Все моды уже установлены", totalBytes, totalBytes);
            return (success, errors);
        }

        // Учитываем уже имеющиеся байты частично скачанных файлов для плавного прогресса
        long initialDownloadedBytes = 0;
        foreach (var m in queue)
        {
            string dest = Path.Combine(modsPath, m.Name);
            if (File.Exists(dest))
            {
                try
                {
                    long len = new FileInfo(dest).Length;
                    if (m.Size <= 0 || len < m.Size)
                    {
                        initialDownloadedBytes += len;
                    }
                }
                catch { }
            }
        }

        long totalDownloadedBytes = initialDownloadedBytes;
        int lastReportedPercent = -1;

        void ReportProgress(string currentFile)
        {
            long currentTotal = Interlocked.Read(ref totalDownloadedBytes);
            int percent = totalBytes > 0 ? (int)Math.Min(100, Math.Round((double)currentTotal / totalBytes * 100)) : 0;
            if (percent != lastReportedPercent)
            {
                lastReportedPercent = percent;
                onProgress(percent, currentFile, currentTotal, totalBytes);
            }
        }

        using var semaphore = new SemaphoreSlim(DownloadConcurrency, DownloadConcurrency);
        var tasks = queue.Select(async mod =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                string dest = Path.Combine(modsPath, mod.Name);
                if (File.Exists(dest) && (mod.Size <= 0 || new FileInfo(dest).Length == mod.Size))
                {
                    Interlocked.Increment(ref success);
                    return;
                }

                await DownloadSingleFileAsync(mod.DownloadUrl, dest, mod.Size, bytesDelta =>
                {
                    Interlocked.Add(ref totalDownloadedBytes, bytesDelta);
                    ReportProgress(mod.Name);
                }, ct);

                ReportProgress(mod.Name);
                Interlocked.Increment(ref success);
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        onProgress(100, "Завершение...", totalBytes, totalBytes);

        return (success, errors);
    }

    private static async Task DownloadSingleFileAsync(
        string url,
        string destPath,
        long expectedSize,
        Action<int> onBytesRead,
        CancellationToken ct)
    {
        long existingLength = 0;
        if (File.Exists(destPath))
        {
            try
            {
                existingLength = new FileInfo(destPath).Length;
                if (expectedSize > 0 && existingLength > expectedSize)
                {
                    File.Delete(destPath);
                    existingLength = 0;
                }
            }
            catch { }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Если Range не поддерживается или вернулась ошибка 416, качаем заново
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(destPath);
            await DownloadSingleFileAsync(url, destPath, expectedSize, onBytesRead, ct);
            return;
        }

        response.EnsureSuccessStatusCode();

        bool isAppend = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, isAppend ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                onBytesRead(bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public int DeleteAllMods(string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return 0;
        }

        // Строгая проверка безопасности: удаление разрешено исключительно из папки ~mods
        var dirInfo = new DirectoryInfo(modsPath);
        if (!dirInfo.Name.Equals("~mods", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Удаление разрешено только из папки ~mods! Текущая папка: {modsPath}");
        }

        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(modsPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // Ignore if in use
                }
            }
        }

        return removed;
    }
}
