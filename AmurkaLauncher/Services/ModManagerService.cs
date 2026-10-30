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
    private const string Repository = "AMURKA-PVE-SCUM/amurka-pve-mods";
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
        // Static catalog is generated in the mods repository by GitHub Actions.
        // Client machines never need a token or an API request in normal operation.
        using var catalogResponse = await Client.GetAsync($"https://raw.githubusercontent.com/{Repository}/main/mods.json");
        if (catalogResponse.IsSuccessStatusCode)
        {
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            var root = catalog.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidOperationException("Неизвестный формат каталога модов.");
            string catalogRevision = root.GetProperty("revision").GetString()!;
            if (catalogRevision.Length != 40 || !catalogRevision.All(Uri.IsHexDigit))
                throw new InvalidOperationException("В каталоге модов указана неверная версия сборки.");
            return ParseModFiles(root.GetProperty("files"), categorySet, catalogRevision);
        }
        // Compatibility fallback only while the static catalog has not been set up.
        if (catalogResponse.StatusCode != HttpStatusCode.NotFound)
            throw new InvalidOperationException("Не удалось загрузить каталог модов. Проверьте подключение и повторите попытку.");
        // Pin downloads to the commit used to read the catalog.
        using var commitResponse = await Client.GetAsync($"https://api.github.com/repos/{Repository}/commits/main");
        if (commitResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub временно ограничил запросы. Статический каталог модов ещё не доступен. Повторите проверку позже.");
        commitResponse.EnsureSuccessStatusCode();
        using var commit = JsonDocument.Parse(await commitResponse.Content.ReadAsStringAsync());
        string revision = commit.RootElement.GetProperty("sha").GetString()!;
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/git/trees/{revision}?recursive=1");
        response.EnsureSuccessStatusCode();
        using var tree = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (tree.RootElement.GetProperty("truncated").GetBoolean())
            throw new InvalidOperationException("GitHub вернул неполный список модов. Синхронизация остановлена.");
        return ParseModFiles(tree.RootElement.GetProperty("tree"), categorySet, revision);
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
                DownloadUrl = $"https://raw.githubusercontent.com/{Repository}/{revision}/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}"
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
        return Directory.EnumerateFiles(path).Where(IsModFile).Select(Path.GetFileName).OfType<string>().ToList();
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
        foreach (var mod in mods) mod.IsInstalled = !string.IsNullOrWhiteSpace(path) && await IsCurrentAsync(mod, Path.Combine(path, mod.Name), ct);
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
            foreach (var mod in mods) mod.IsInstalled = true;
            foreach (var mod in mods) state[mod.Name] = new ManagedMod { Name = mod.Name, Category = mod.Category, Sha = mod.Sha };
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
    }
    private static Dictionary<string, ManagedMod> ReadState(string path)
    {
        string file = Path.Combine(path, StateFile);
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        var entries = JsonSerializer.Deserialize<List<ManagedMod>>(File.ReadAllText(file))
            ?? throw new InvalidOperationException("Повреждён список установленных модов.");
        foreach (var entry in entries) ValidateName(entry.Name);
        return entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }
    private static void WriteState(string path, Dictionary<string, ManagedMod> state)
    {
        string file = Path.Combine(path, StateFile);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state.Values.ToList()));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public void RecordVerifiedInstalledMods(IEnumerable<ModItem> mods, string modsPath)
    {
        ValidateModsPath(modsPath);
        if (!Directory.Exists(modsPath)) return;
        var state = ReadState(modsPath);
        foreach (var mod in mods.Where(m => m.IsInstalled))
            state[mod.Name] = new ManagedMod { Name = mod.Name, Category = mod.Category, Sha = mod.Sha };
        WriteState(modsPath, state);
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
