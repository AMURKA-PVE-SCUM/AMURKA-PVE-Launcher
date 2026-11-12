using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Services;

public class ModManagerService
{
    private const string RepositoryRaw = "https://gitverse.ru/api/repos/DOMO/amurka-pve-mods/raw";
    private const int BufferSize = 81920;
    private const string StateFile = ".amurka-installed.json";
    private readonly HttpClient Client;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public ModManagerService(HttpClient? client = null) => Client = client ?? SharedClient;
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AMURKA-Launcher/2.1");
        return client;
    }

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
        // GitVerse's public raw endpoint works without a player account or API token.
        // Resolve the catalog on main, then pin every download to its immutable commit.
        using var response = await Client.GetAsync($"{RepositoryRaw}/branch/main/mods.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Не удалось загрузить каталог модов с GitVerse. Проверьте подключение и повторите попытку.");
        try
        {
            using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = catalog.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidOperationException("Неизвестный формат каталога модов.");
            string? revision = root.GetProperty("revision").GetString();
            if (revision == null || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
                throw new InvalidOperationException("В каталоге модов указана неверная версия сборки.");
            return ParseModFiles(root.GetProperty("files"), categorySet, revision);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("GitVerse вернул некорректный каталог модов. Повторите проверку позже.", ex);
        }
    }

    private static List<ModItem> ParseModFiles(JsonElement files, HashSet<string> categorySet, string revision)
    {
        var result = new List<ModItem>();
        foreach (var item in files.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() != "blob") continue;
            var parts = item.GetProperty("path").GetString()!.Split('/', 2);
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
                DownloadUrl = $"{RepositoryRaw}/commit/{revision}/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}"
            });
        }
        if (result.Count == 0) throw new InvalidOperationException("В репозитории не найдены моды. Синхронизация остановлена.");
        if (result.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("В категориях найдены моды с одинаковыми именами.");
        return result;
    }

    private static bool IsModFile(string name) => name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);
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
        catch
        {
            return [];
        }
    }

    // Git's blob SHA-1 includes a header; a plain file SHA-1 is different.
    public static async Task<string> ComputeGitBlobHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + stream.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0) hash.AppendData(buffer, 0, count);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static async Task<bool> IsCurrentAsync(ModItem mod, string path, CancellationToken ct)
    {
        ValidateName(mod.Name);
        if (mod.Sha.Length != 40 || !mod.Sha.All(Uri.IsHexDigit)) throw new InvalidOperationException($"Нет контрольной суммы для {mod.Name}");
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == mod.Size &&
                string.Equals(await ComputeGitBlobHashAsync(path, ct), mod.Sha, StringComparison.OrdinalIgnoreCase);
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
            catch
            {
                mod.IsInstalled = false;
            }
        }

        if (stateModified)
        {
            try { WriteState(path, state); } catch { }
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
            var queue = mods.Where(m => !m.IsInstalled).ToList();
            long total = queue.Sum(m => m.Size), downloaded = 0;
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
                        await DownloadSingleFileAsync(mod, temp, delta =>
                        {
                            downloaded += delta;
                            onProgress(total > 0 ? (int)Math.Min(99, downloaded * 100 / total) : 0, mod.Name, downloaded, total);
                        }, ct);
                        if (!await IsCurrentAsync(mod, temp, ct))
                        {
                            File.Delete(temp);
                            throw new IOException($"Контрольная сумма не совпала: {mod.Name}");
                        }
                    }
                    staged.Add((mod, temp));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    errors++;
                    onProgress(0, $"Ошибка {mod.Name}: {ex.Message}", downloaded, total);
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
        response.EnsureSuccessStatusCode();
        bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != mod.Size))
            throw new IOException("Сервер вернул неверный диапазон файла.");
        if (append) onBytes(offset);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(temp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                if (output.Length + count > mod.Size) throw new IOException("Размер загрузки превышает ожидаемый.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                onBytes(count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public sealed class ManagedMod
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Sha { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
    }

    private static Dictionary<string, ManagedMod> ReadState(string path)
    {
        string file = Path.Combine(path, StateFile);
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string content = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(content)) return new(StringComparer.OrdinalIgnoreCase);

            var entries = JsonSerializer.Deserialize<List<ManagedMod>>(content);
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
        catch
        {
            // Безопасный возврат пустого состояния при повреждении файла
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteState(string path, Dictionary<string, ManagedMod> state)
    {
        try
        {
            string file = Path.Combine(path, StateFile);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state.Values.ToList()));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch { }
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
        var allowedNames = catalog.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        catch
        {
            return [];
        }
    }

    public async Task<int> RemoveObsoleteAndUntrackedModsAsync(List<ModItem> catalog, string modsPath)
    {
        ValidateModsPath(modsPath);
        if (!Directory.Exists(modsPath)) return 0;

        int removed = 0;
        var allowedNames = catalog.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                    catch { }
                }
            }
        }
        catch { }

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
        catch { }

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
            if (File.Exists(path) && !string.Equals(await ComputeGitBlobHashAsync(path), old.Sha, StringComparison.OrdinalIgnoreCase)) continue;
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
                string.Equals(await ComputeGitBlobHashAsync(path), mod.Sha, StringComparison.OrdinalIgnoreCase)) result.Add(mod);
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
            catch
            {
                // Ignore if in use
            }
        }

        return removed;
    }
}
