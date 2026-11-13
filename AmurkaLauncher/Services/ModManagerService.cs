using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amurka.Shared.Core.Domain.Results;
using Amurka.Shared.IO;
using Amurka.Shared.Network;
using Amurka.Shared.Resilience;
using Amurka.Shared.Security;
using AmurkaLauncher.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Core.Serialization;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Services;

public sealed class ModManagerService : IModSyncEngine, IModCatalogRepository, IInstalledStateRepository, IDisposable
{
    public const string YandexDiskPublicUrl = "https://disk.yandex.ru/d/2FbSACljtnvwtg";
    private const string YandexDiskApiBase = "https://cloud-api.yandex.net/v1/disk/public/resources";
    private const string GitVerseHost = "https://gitverse.ru";
    private const string RepositoryOwner = "DOMO";
    private const string RepositoryName = "amurka-pve-mods";
    private const string DefaultBranch = "main";
    private const string RawApiBaseUrl = $"{GitVerseHost}/api/repos/{RepositoryOwner}/{RepositoryName}/raw/branch/{DefaultBranch}";
    private const int BufferSize = 524288; // 512 KB high-throughput buffer
    private const long MultiStreamThreshold = 20 * 1024 * 1024; // 20 MB порог для параллельной многопоточной загрузки
    private const int ParallelStreamCount = 3; // 3 одновременных потока для ускорения больших файлов в 3 раза
    private const string StateFile = ".amurka-installed.json";
    private static readonly string CatalogCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "amurka-launcher",
        "catalog-cache.json");
    private readonly HttpClient Client;
    private readonly ILogger<ModManagerService>? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public ModManagerService(HttpClient? client = null, ILogger<ModManagerService>? logger = null)
    {
        Client = client ?? SharedClient;
        _logger = logger;
    }

    private static readonly HttpClient SharedClient = HttpClientDefaults.CreateClient(TimeSpan.FromMinutes(15));

    // Методы реализации интерфейса IModSyncEngine с Value Object ModsPath
    public Task RefreshInstalledStateAsync(IEnumerable<ModItem> mods, ModsPath modsPath, CancellationToken ct = default)
        => RefreshInstalledStateAsync(mods, modsPath.Value, ct);

    public Task<(bool isSynced, List<ModItem> missingOrCorrupt, List<string> obsoleteFiles)> CheckSynchronizationAsync(
        List<ModItem> catalog,
        ModsPath modsPath,
        CancellationToken ct = default)
        => CheckSynchronizationAsync(catalog, modsPath.Value, ct);

    public async Task<Result<(int success, int errors)>> DownloadAllModsAsync(
        List<ModItem> mods,
        ModsPath modsPath,
        IProgress<SyncProgressReport> progress,
        CancellationToken ct = default)
    {
        try
        {
            var res = await DownloadAllModsAsync(mods, modsPath.Value, (percent, file, dl, total) =>
            {
                progress.Report(new SyncProgressReport(percent, file, dl, total, $"Загрузка: {file} ({percent}%)"));
            }, ct);

            return Result<(int success, int errors)>.Success(res);
        }
        catch (Exception ex)
        {
            return Result<(int success, int errors)>.Failure(ex.Message);
        }
    }

    public List<string> FindUntrackedOrObsoleteFiles(List<ModItem> catalog, ModsPath modsPath)
        => FindUntrackedOrObsoleteFiles(catalog, modsPath.Value);

    public Task<int> RemoveObsoleteAndUntrackedModsAsync(List<ModItem> catalog, ModsPath modsPath, CancellationToken ct = default)
        => RemoveObsoleteAndUntrackedModsAsync(catalog, modsPath.Value);

    public Task<List<ManagedMod>> FindObsoleteModsAsync(List<ModItem> catalog, ModsPath modsPath, CancellationToken ct = default)
        => FindObsoleteModsAsync(catalog, modsPath.Value);

    public int DeleteAllMods(ModsPath modsPath)
        => DeleteAllMods(modsPath.Value);

    Dictionary<string, ManagedMod> IInstalledStateRepository.ReadState(ModsPath modsPath)
        => ReadState(modsPath.Value);

    void IInstalledStateRepository.WriteState(ModsPath modsPath, Dictionary<string, ManagedMod> state)
        => WriteState(modsPath.Value, state);

    void IInstalledStateRepository.RecordVerifiedMods(IEnumerable<ModItem> mods, ModsPath modsPath)
        => RecordVerifiedInstalledMods(mods, modsPath.Value);

    List<string> IInstalledStateRepository.ScanInstalledModFiles(ModsPath modsPath)
        => ScanInstalledMods(modsPath.Value);

    public List<ModCategory> GetDefaultCategories()
    {
        return
        [
            new ModCategory { Id = "core", Label = "Основные", Required = true, IsSelected = true }
        ];
    }

    public async Task<List<ModItem>> FetchAvailableModsAsync(IEnumerable<ModCategory>? categories = null, CancellationToken ct = default)
    {
        var categorySet = (categories ?? GetDefaultCategories()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string catalogUrl = $"{YandexDiskApiBase}?public_key={Uri.EscapeDataString(YandexDiskPublicUrl)}&limit=200";
        string? json = null;

        try
        {
            json = await RetryPolicy.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, innerCt);

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(3);
                    if (response.Headers.RetryAfter?.Delta is { } delta) delay = delta;
                    _logger?.LogWarning("Облачное хранилище вернуло HTTP 429 при загрузке каталога. Ожидание {DelaySec:F1} сек...", delay.TotalSeconds);
                    await Task.Delay(delay, innerCt);
                    throw new HttpRequestException("HTTP 429 Too Many Requests при загрузке каталога", null, (HttpStatusCode)429);
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(innerCt);
            }, maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(800), logger: _logger, operationName: "FetchModsCatalog", ct: ct);

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    AtomicFileStorage.WriteAllTextAtomic(CatalogCachePath, json);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Не удалось сохранить кэш каталога модов.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Сбой загрузки каталога модов. Попытка восстановления из локального кэша.");
            if (File.Exists(CatalogCachePath))
            {
                try
                {
                    json = File.ReadAllText(CatalogCachePath);
                }
                catch (Exception cacheEx)
                {
                    _logger?.LogError(cacheEx, "Не удалось прочитать локальный кэш каталога модов.");
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException($"Не удалось загрузить каталог модов ({ex.Message}). Проверьте подключение к сети и повторите попытку.", ex);
            }
        }

        using var catalog = JsonDocument.Parse(json);
        var root = catalog.RootElement;
        if (root.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("items", out var items))
        {
            return ParseYandexDiskModFiles(items, categorySet);
        }
        if (root.TryGetProperty("schemaVersion", out var sv) && sv.GetInt32() == 1)
        {
            string catalogRevision = root.GetProperty("revision").GetString()!;
            if (catalogRevision.Length != 40 || !catalogRevision.All(Uri.IsHexDigit))
                throw new InvalidOperationException("В каталоге модов указана неверная версия сборки.");
            return ParseModFiles(root.GetProperty("files"), categorySet, catalogRevision);
        }
        throw new InvalidOperationException("Неизвестный формат каталога модов.");
    }

    private static List<ModItem> ParseYandexDiskModFiles(JsonElement items, HashSet<string> categorySet)
    {
        var result = new List<ModItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() != "file") continue;
            string name = item.GetProperty("name").GetString()!;
            if (!IsModFile(name)) continue;
            ValidateName(name);

            long size = item.GetProperty("size").GetInt64();
            string sha = string.Empty;
            if (item.TryGetProperty("sha256", out var sProp) && !string.IsNullOrWhiteSpace(sProp.GetString()))
            {
                sha = sProp.GetString()!;
            }

            if (size < 0 || (sha.Length != 64 && sha.Length != 40) || !sha.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"В каталоге неверные данные файла: {name}");
            }

            string downloadUrl = item.GetProperty("file").GetString()!;
            const string defaultCategory = "core";
            if (!categorySet.Contains(defaultCategory)) continue;

            result.Add(new ModItem
            {
                Name = name,
                Category = defaultCategory,
                Size = size,
                Sha = sha,
                DownloadUrl = downloadUrl
            });
        }

        if (result.Count == 0) throw new InvalidOperationException("В репозитории не найдены моды. Синхронизация остановлена.");
        if (result.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("В категориях найдены моды с одинаковыми именами.");
        return result;
    }

    private static List<ModItem> ParseModFiles(JsonElement files, HashSet<string> categorySet, string revision)
    {
        var result = new List<ModItem>();
        foreach (var item in files.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() != "blob") continue;
            string rawPath = item.GetProperty("path").GetString()!;
            var parts = rawPath.Split('/', 2);
            if (parts.Length != 2 || !categorySet.Contains(parts[0]) || !IsModFile(parts[1])) continue;
            ValidateName(parts[1]);
            long size = item.GetProperty("size").GetInt64();
            string sha = item.GetProperty("sha").GetString()!;
            if (size < 0 || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"В каталоге неверные данные файла: {parts[1]}");
            result.Add(new ModItem
            {
                Name = parts[1], Category = parts[0], Size = size,
                Sha = sha,
                DownloadUrl = $"{RawApiBaseUrl}/{Uri.EscapeDataString(parts[0])}%2F{Uri.EscapeDataString(parts[1])}"
            });
        }
        if (result.Count == 0) throw new InvalidOperationException("В репозитории не найдены моды. Синхронизация остановлена.");
        if (result.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("В категориях найдены моды с одинаковыми именами.");
        return result;
    }

    private static bool IsModFile(string name) => PathSecurityGuard.HasModExtension(name);
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name != Path.GetFileName(name) || !IsModFile(name))
            throw new InvalidOperationException($"Недопустимое имя мода: {name}");
    }
    private static void ValidateModsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !new DirectoryInfo(path).Name.Equals("~mods", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Установка и удаление разрешены только в папке ~mods.");
    }
    public List<string> ScanInstalledMods(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return [];
        try
        {
            return Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith(".amurka-", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).EndsWith(".bak", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Where(IsModFile)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Не удалось просканировать папку модов '{ModsPath}'.", path);
            return [];
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Делегирует глобальному Zero-Allocation классу GitBlobHasher из Amurka.Shared
    public static Task<string> ComputeGitBlobHashAsync(string path, CancellationToken ct = default)
        => GitBlobHasher.ComputeFileHashAsync(path, ct);

    private static async Task<bool> VerifyFileHashMatchesAsync(string path, string expectedSha, CancellationToken ct = default)
    {
        if (expectedSha.Length == 64)
            return string.Equals(await ComputeSha256Async(path, ct), expectedSha, StringComparison.OrdinalIgnoreCase);
        return string.Equals(await ComputeGitBlobHashAsync(path, ct), expectedSha, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsCurrentAsync(ModItem mod, string path, CancellationToken ct)
    {
        ValidateName(mod.Name);
        if ((mod.Sha.Length != 64 && mod.Sha.Length != 40) || !mod.Sha.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"Нет контрольной суммы для {mod.Name}");
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == mod.Size &&
                await VerifyFileHashMatchesAsync(path, mod.Sha, ct);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public async Task RefreshInstalledStateAsync(IEnumerable<ModItem> mods, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            foreach (var mod in mods) mod.IsInstalled = false;
            return;
        }

        var state = ReadState(path);
        bool stateModified = false;

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            string filePath = Path.Combine(path, mod.Name);

            try
            {
                if (!File.Exists(filePath))
                {
                    mod.IsInstalled = false;
                    if (state.Remove(mod.Name)) stateModified = true;
                    continue;
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length != mod.Size)
                {
                    mod.IsInstalled = false;
                    continue;
                }

                // Быстрая проверка: если файл зафиксирован в состоянии и не менялся по размеру и штампу времени
                if (state.TryGetValue(mod.Name, out var cached) &&
                    string.Equals(cached.Sha, mod.Sha, StringComparison.OrdinalIgnoreCase) &&
                    cached.Size == mod.Size &&
                    cached.LastWriteTimeUtcTicks != 0 &&
                    cached.LastWriteTimeUtcTicks == fileInfo.LastWriteTimeUtc.Ticks)
                {
                    mod.IsInstalled = true;
                    continue;
                }

                // Полная валидация содержимого через хеш Git blob
                string actualHash = await ComputeGitBlobHashAsync(filePath, ct);
                if (string.Equals(actualHash, mod.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    mod.IsInstalled = true;
                    state[mod.Name] = new ManagedMod
                    {
                        Name = mod.Name,
                        Category = mod.Category,
                        Sha = mod.Sha,
                        Size = fileInfo.Length,
                        LastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks
                    };
                    stateModified = true;
                }
                else
                {
                    mod.IsInstalled = false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Не удалось проверить состояние мода '{ModName}'.", mod.Name);
                mod.IsInstalled = false;
            }
        }

        if (stateModified)
        {
            WriteState(path, state);
        }
    }

    public async Task<(int success, int errors)> DownloadAllModsAsync(List<ModItem> mods, string modsPath,
        Action<int, string, long, long> onProgress, CancellationToken ct = default)
    {
        ValidateModsPath(modsPath);
        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(modsPath);
            var state = ReadState(modsPath);
            await RefreshInstalledStateAsync(mods, modsPath, ct);
            var queue = mods.Where(m => !m.IsInstalled).OrderByDescending(m => m.Size).ToList();
            long total = queue.Sum(m => m.Size);
            long completedBytes = 0;
            int errors = 0;
            var staged = new List<(ModItem Mod, string Temp)>();
            // Validate the whole selection before replacing any installed file.
            foreach (var mod in queue)
            {
                ct.ThrowIfCancellationRequested();
                string temp = Path.Combine(modsPath, $".amurka-{mod.Name}-{mod.Sha}.part");
                try
                {
                    if (!await IsCurrentAsync(mod, temp, ct))
                    {
                        long lastReportTicks = 0;
                        void ReportProgress(long currentFileBytes, bool force = false)
                        {
                            long now = Environment.TickCount64;
                            if (force || now - lastReportTicks >= 80)
                            {
                                lastReportTicks = now;
                                long currentTotal = completedBytes + currentFileBytes;
                                int percent = total > 0 ? (int)Math.Clamp(currentTotal * 100 / total, 0, 99) : 0;
                                onProgress(percent, mod.Name, currentTotal, total);
                            }
                        }

                        long existingPartBytes = File.Exists(temp) ? new FileInfo(temp).Length : 0;
                        if (existingPartBytes > 0 && existingPartBytes < mod.Size)
                        {
                            long currentTotal = completedBytes + existingPartBytes;
                            int percent = total > 0 ? (int)Math.Clamp(currentTotal * 100 / total, 0, 99) : 0;
                            onProgress(percent, $"{mod.Name} (докачка с {existingPartBytes / (1024.0 * 1024.0):F1} МБ)", currentTotal, total);
                        }

                        await RetryPolicy.ExecuteAsync(async innerCt =>
                        {
                            await DownloadSingleFileAsync(mod, temp, fileBytes =>
                            {
                                ReportProgress(fileBytes);
                            }, innerCt);

                            if (!await IsCurrentAsync(mod, temp, innerCt))
                            {
                                File.Delete(temp);
                                throw new IOException($"Контрольная сумма не совпала: {mod.Name}");
                            }
                            return true;
                        }, maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(500), logger: _logger, operationName: $"DownloadMod_{mod.Name}", ct: ct);

                        ReportProgress(mod.Size, force: true);
                    }
                    staged.Add((mod, temp));
                    completedBytes += mod.Size;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    errors++;
                    _logger?.LogError(ex, "Критический сбой скачивания мода '{ModName}'.", mod.Name);
                    onProgress(0, $"Ошибка {mod.Name}: {ex.Message}", completedBytes, total);
                }
            }
            if (errors > 0) return (0, errors);
            var backups = new List<(string Destination, string? Backup)>();
            bool preserveBackups = false;
            try
            {
                // Prepare rollback copies before touching any installed .pak/.sig pair.
                foreach (var (mod, _) in staged)
                {
                    string dest = Path.Combine(modsPath, mod.Name);
                    string? backup = File.Exists(dest) ? dest + "." + Guid.NewGuid().ToString("N") + ".bak" : null;
                    if (backup != null) File.Copy(dest, backup);
                    backups.Add((dest, backup));
                }
                int applied = 0;
                try
                {
                    foreach (var (mod, temp) in staged)
                    {
                        File.Move(temp, Path.Combine(modsPath, mod.Name), overwrite: true);
                        applied++;
                    }
                }
                catch
                {
                    try
                    {
                        foreach (var (dest, backup) in backups.Take(applied).Reverse())
                        {
                            if (backup != null) File.Move(backup, dest, overwrite: true);
                            else File.Delete(dest);
                        }
                    }
                    catch { preserveBackups = true; throw; }
                    throw;
                }
            }
            finally
            {
                if (!preserveBackups)
                    foreach (var (_, backup) in backups)
                        if (backup != null && File.Exists(backup)) File.Delete(backup);
            }
            foreach (var mod in mods)
            {
                mod.IsInstalled = true;
                string modFilePath = Path.Combine(modsPath, mod.Name);
                var fi = new FileInfo(modFilePath);
                state[mod.Name] = new ManagedMod
                {
                    Name = mod.Name,
                    Category = mod.Category,
                    Sha = mod.Sha,
                    Size = fi.Exists ? fi.Length : mod.Size,
                    LastWriteTimeUtcTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0
                };
            }
            WriteState(modsPath, state);
            onProgress(100, "Моды актуальны", total, total);
            return (mods.Count, 0);
        }
        finally { _syncLock.Release(); }
    }
    private async Task DownloadSingleFileAsync(ModItem mod, string temp, Action<long> onBytes, CancellationToken ct)
    {
        bool hasChunkFiles = File.Exists($"{temp}.c0") || File.Exists($"{temp}.c1") || File.Exists($"{temp}.c2");
        bool hasIncompleteSinglePart = File.Exists(temp) && new FileInfo(temp).Length > 0;

        // Если файл большой и нет начатой однопоточной загрузки (или уже есть начатые чанки) — качаем в 3 параллельных потока
        if (mod.Size >= MultiStreamThreshold && (!hasIncompleteSinglePart || hasChunkFiles))
        {
            try
            {
                await DownloadLargeFileMultiStreamAsync(mod, temp, onBytes, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Многопоточная загрузка для '{ModName}' переключена на стандартный поток.", mod.Name);
            }
        }

        long offset = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (offset >= mod.Size) { File.Delete(temp); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, mod.DownloadUrl);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(temp);
            await DownloadSingleFileAsync(mod, temp, onBytes, ct);
            return;
        }

        if (response.StatusCode == (HttpStatusCode)429) // Too Many Requests (Rate limit)
        {
            TimeSpan delay = TimeSpan.FromSeconds(3);
            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                delay = delta;
            }
            else if (response.Headers.RetryAfter?.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero) delay = diff;
            }

            _logger?.LogWarning("Яндекс Диск вернул HTTP 429 (Превышен лимит запросов). Ожидание {DelaySec:F1} сек перед повтором файла {ModName}...", delay.TotalSeconds, mod.Name);
            await Task.Delay(delay, ct);
            throw new HttpRequestException("HTTP 429 Too Many Requests (Rate Limit). Повтор запроса...", null, (HttpStatusCode)429);
        }

        response.EnsureSuccessStatusCode();
        bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var cr = response.Content.Headers.ContentRange;
            if (cr != null && cr.From.HasValue && cr.From.Value != offset)
            {
                throw new IOException($"Сервер вернул неверное начальное смещение диапазона: ожидалось {offset}, получено {cr.From.Value}.");
            }
            if (cr != null && cr.HasLength && cr.Length.HasValue && cr.Length.Value != mod.Size)
            {
                throw new IOException($"Сервер вернул неверный общий размер файла: ожидался {mod.Size}, получено {cr.Length.Value}.");
            }
        }
        if (append) onBytes(offset);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            temp,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (!append && mod.Size > 0)
        {
            output.SetLength(mod.Size);
            output.Seek(0, SeekOrigin.Begin);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                if (output.Position + count > mod.Size) throw new IOException("Размер загрузки превышает ожидаемый.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                onBytes(output.Position);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task DownloadLargeFileMultiStreamAsync(ModItem mod, string temp, Action<long> onBytes, CancellationToken ct)
    {
        int chunkCount = ParallelStreamCount;
        long chunkSize = (mod.Size + chunkCount - 1) / chunkCount;
        string[] chunkPaths = new string[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            chunkPaths[i] = $"{temp}.c{i}";
        }

        long[] chunkProgress = new long[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            if (File.Exists(chunkPaths[i]))
            {
                chunkProgress[i] = new FileInfo(chunkPaths[i]).Length;
            }
        }

        void ReportCombinedProgress()
        {
            long sum = 0;
            for (int i = 0; i < chunkCount; i++) sum += Interlocked.Read(ref chunkProgress[i]);
            onBytes(Math.Min(mod.Size, sum));
        }

        ReportCombinedProgress();

        var tasks = new Task[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            int index = i;
            long start = index * chunkSize;
            long end = Math.Min((index + 1) * chunkSize - 1, mod.Size - 1);
            long expectedLen = end - start + 1;
            string chunkFile = chunkPaths[index];

            tasks[index] = Task.Run(async () =>
            {
                long offset = File.Exists(chunkFile) ? new FileInfo(chunkFile).Length : 0;
                if (offset > expectedLen) { File.Delete(chunkFile); offset = 0; }
                if (offset == expectedLen)
                {
                    Interlocked.Exchange(ref chunkProgress[index], offset);
                    ReportCombinedProgress();
                    return;
                }

                long requestStart = start + offset;
                using var request = new HttpRequestMessage(HttpMethod.Get, mod.DownloadUrl);
                request.Headers.Range = new RangeHeaderValue(requestStart, end);

                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(chunkFile);
                    offset = 0;
                    Interlocked.Exchange(ref chunkProgress[index], 0);
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, mod.DownloadUrl);
                    retryReq.Headers.Range = new RangeHeaderValue(start, end);
                    using var retryResp = await Client.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct);
                    retryResp.EnsureSuccessStatusCode();
                    await ReadStreamToChunkFileAsync(retryResp, chunkFile, expectedLen, false, written =>
                    {
                        Interlocked.Exchange(ref chunkProgress[index], written);
                        ReportCombinedProgress();
                    }, ct);
                    return;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(3);
                    if (response.Headers.RetryAfter?.Delta is { } delta) delay = delta;
                    await Task.Delay(delay, ct);
                    throw new HttpRequestException("HTTP 429 Too Many Requests при загрузке чанка.", null, (HttpStatusCode)429);
                }

                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new NotSupportedException("Сервер не вернул Partial Content (206) для многопоточного чанка.");
                }

                await ReadStreamToChunkFileAsync(response, chunkFile, expectedLen, offset > 0, written =>
                {
                    Interlocked.Exchange(ref chunkProgress[index], offset + written);
                    ReportCombinedProgress();
                }, ct);
            }, ct);
        }

        await Task.WhenAll(tasks);

        // Объединение скачанных фрагментов в итоговый .part файл
        await using (var output = new FileStream(
            temp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            output.SetLength(mod.Size);
            byte[] mergeBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    await using var chunkStream = new FileStream(
                        chunkPaths[i],
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    int read;
                    while ((read = await chunkStream.ReadAsync(mergeBuffer.AsMemory(0, BufferSize), ct)) > 0)
                    {
                        await output.WriteAsync(mergeBuffer.AsMemory(0, read), ct);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(mergeBuffer);
            }
        }

        // Удаление временных файлов чанков после успешного объединения
        for (int i = 0; i < chunkCount; i++)
        {
            try { if (File.Exists(chunkPaths[i])) File.Delete(chunkPaths[i]); } catch { }
        }

        onBytes(mod.Size);
    }

    private static async Task ReadStreamToChunkFileAsync(
        HttpResponseMessage response,
        string chunkFile,
        long expectedLength,
        bool append,
        Action<long> onChunkBytes,
        CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            chunkFile,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (!append && expectedLength > 0)
        {
            output.SetLength(expectedLength);
            output.Seek(0, SeekOrigin.Begin);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long written = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                if (output.Position + count > expectedLength)
                    throw new IOException("Размер данных чанка превышает ожидаемый.");

                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                written += count;
                onChunkBytes(written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Dictionary<string, ManagedMod> ReadState(string path)
    {
        string file = Path.Combine(path, StateFile);
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string content = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(content)) return new(StringComparer.OrdinalIgnoreCase);

            var entries = JsonSerializer.Deserialize(content, LauncherJsonContext.Default.ListManagedMod);
            if (entries == null) return new(StringComparer.OrdinalIgnoreCase);

            var dict = new Dictionary<string, ManagedMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) && IsModFile(entry.Name))
                {
                    dict[entry.Name] = entry;
                }
            }
            return dict;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Повреждён или недоступен файл состояния '{StateFile}'; состояние переинициализируется.", file);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteState(string path, Dictionary<string, ManagedMod> state)
    {
        try
        {
            string file = Path.Combine(path, StateFile);
            string json = JsonSerializer.Serialize(state.Values.ToList(), LauncherJsonContext.Default.ListManagedMod);
            AtomicFileStorage.WriteAllTextAtomic(file, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Не удалось сохранить состояние установленных модов в '{ModsPath}'.", path);
        }
    }

    public void RecordVerifiedInstalledMods(IEnumerable<ModItem> mods, string modsPath)
    {
        ValidateModsPath(modsPath);
        if (!Directory.Exists(modsPath)) return;
        var state = ReadState(modsPath);
        bool modified = false;

        foreach (var mod in mods.Where(m => m.IsInstalled))
        {
            string filePath = Path.Combine(modsPath, mod.Name);
            long size = mod.Size;
            long ticks = 0;
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                size = fi.Length;
                ticks = fi.LastWriteTimeUtc.Ticks;
            }

            state[mod.Name] = new ManagedMod
            {
                Name = mod.Name,
                Category = mod.Category,
                Sha = mod.Sha,
                Size = size,
                LastWriteTimeUtcTicks = ticks
            };
            modified = true;
        }

        if (modified)
        {
            WriteState(modsPath, state);
        }
    }

    public List<string> FindUntrackedOrObsoleteFiles(List<ModItem> catalog, string modsPath)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath)) return [];
        var allowedNames = catalog.Select(m => m.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(modsPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name)) return false;
                    if (name.Equals(StateFile, StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.StartsWith(".amurka-", StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;
                    return !allowedNames.Contains(name);
                })
                .Select(Path.GetFileName)
                .Select(f => f!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Не удалось просканировать сторонние файлы в '{ModsPath}'.", modsPath);
            return [];
        }
    }

    public async Task<int> RemoveObsoleteAndUntrackedModsAsync(List<ModItem> catalog, string modsPath)
    {
        ValidateModsPath(modsPath);
        if (!Directory.Exists(modsPath)) return 0;

        int removed = 0;
        var allowedNames = catalog.Select(m => m.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var files = Directory.EnumerateFiles(modsPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals(StateFile, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in files)
            {
                string name = Path.GetFileName(file);
                if (name.Equals(StateFile, StringComparison.OrdinalIgnoreCase)) continue;

                // Защищаем активные частичные загрузки модов из текущего каталога
                if (name.StartsWith(".amurka-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    if (catalog.Any(m => name.StartsWith($".amurka-{m.Name}-", StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                if (!allowedNames.Contains(name))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger?.LogWarning(ex, "Не удалось удалить устаревший или сторонний файл '{FilePath}'.", file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Ошибка перечисления файлов при очистке '{ModsPath}'.", modsPath);
        }

        try
        {
            var state = ReadState(modsPath);
            bool stateChanged = false;
            foreach (var key in state.Keys.ToList())
            {
                if (!allowedNames.Contains(key))
                {
                    state.Remove(key);
                    stateChanged = true;
                }
            }
            if (stateChanged)
            {
                WriteState(modsPath, state);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Не удалось обновить файл состояния после удаления устаревших модов.");
        }

        return removed;
    }

    public async Task<(bool isSynced, List<ModItem> missingOrCorrupt, List<string> obsoleteFiles)> CheckSynchronizationAsync(
        List<ModItem> catalog,
        string modsPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
        {
            return (false, catalog, []);
        }

        await RefreshInstalledStateAsync(catalog, modsPath, ct);
        var missingOrCorrupt = catalog.Where(m => !m.IsInstalled).ToList();
        var obsoleteFiles = FindUntrackedOrObsoleteFiles(catalog, modsPath);

        bool isSynced = missingOrCorrupt.Count == 0 && obsoleteFiles.Count == 0;
        return (isSynced, missingOrCorrupt, obsoleteFiles);
    }

    public Task RemoveObsoleteModsAsync(List<ModItem> catalog, string modsPath)
    {
        return RemoveObsoleteAndUntrackedModsAsync(catalog, modsPath);
    }

    public async Task RemoveObsoleteModsAsync(List<ModItem> catalog, IEnumerable<string> selectedCategories, string modsPath)
    {
        ValidateModsPath(modsPath);
        if (!Directory.Exists(modsPath)) return;
        var selected = selectedCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = catalog.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = ReadState(modsPath);
        foreach (var old in state.Values.ToList())
        {
            if (!selected.Contains(old.Category) || current.Contains(old.Name)) continue;
            string path = Path.Combine(modsPath, old.Name);
            if (File.Exists(path) && !await VerifyFileHashMatchesAsync(path, old.Sha)) continue;
            File.Delete(path);
            state.Remove(old.Name);
        }
        WriteState(modsPath, state);
    }

    public async Task<List<ManagedMod>> FindObsoleteModsAsync(List<ModItem> catalog, string modsPath)
    {
        var result = new List<ManagedMod>();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath)) return result;
        var names = catalog.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in ReadState(modsPath).Values)
        {
            string path = Path.Combine(modsPath, mod.Name);
            if (!names.Contains(mod.Name) && File.Exists(path) &&
                await VerifyFileHashMatchesAsync(path, mod.Sha)) result.Add(mod);
        }
        return result;
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
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Не удалось удалить файл мода '{FilePath}' при полной очистке.", file);
            }
        }

        return removed;
    }

    public void Dispose()
    {
        _syncLock.Dispose();
    }
}
