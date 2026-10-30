using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AmurkaLauncher.Models;
using AmurkaLauncher.Services;

// No test packages required. Exercises the production service with an in-memory HTTP server.
string root = Path.Combine(AppContext.BaseDirectory, "amurka-sync-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    checks++;
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray())).ToLowerInvariant();
ModItem Mod(string name, byte[] bytes) => new() { Name = name, Category = "core", Size = bytes.Length, Sha = Hash(bytes), DownloadUrl = "https://test/" + name };
string Folder(string name)
{
    string path = Path.Combine(root, name, "~mods");
    Directory.CreateDirectory(path);
    return path;
}
try
{
    Check(Hash([]) == "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", "Git empty blob reference vector");
    byte[] old = Encoding.UTF8.GetBytes("old-data"), updated = Encoding.UTF8.GetBytes("new-data");
    string path = Folder("same-size");
    var mod = Mod("test.pak", updated);
    await File.WriteAllBytesAsync(Path.Combine(path, mod.Name), old);
    var handler = new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(updated) });
    var service = new ModManagerService(new HttpClient(handler));
    await service.RefreshInstalledStateAsync([mod], path);
    Check(!mod.IsInstalled, "same-size changed file needs update");
    var result = await service.DownloadAllModsAsync([mod], path, (_, _, _, _) => { });
    Check(result.errors == 0 && (await File.ReadAllBytesAsync(Path.Combine(path, mod.Name))).SequenceEqual(updated), "updated mod replaces old content");
    await service.DownloadAllModsAsync([mod], path, (_, _, _, _) => { });
    Check(handler.Count == 1, "current file is not downloaded again");
    Check(await ModManagerService.ComputeGitBlobHashAsync(Path.Combine(path, mod.Name)) == mod.Sha, "production hash matches Git blob format");

    string adoption = Folder("adoption");
    await File.WriteAllBytesAsync(Path.Combine(adoption, mod.Name), updated);
    await service.DownloadAllModsAsync([mod], adoption, (_, _, _, _) => { });
    Check(handler.Count == 1, "existing installation is adopted without download");

    string corrupt = Folder("corrupt");
    await File.WriteAllBytesAsync(Path.Combine(corrupt, mod.Name), old);
    var badService = new ModManagerService(new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(old) })));
    result = await badService.DownloadAllModsAsync([mod], corrupt, (_, _, _, _) => { });
    Check(result.errors == 1 && (await File.ReadAllBytesAsync(Path.Combine(corrupt, mod.Name))).SequenceEqual(old), "bad hash leaves installed file intact");

    string resume = Folder("resume");
    string temp = Path.Combine(resume, $".amurka-{mod.Name}-{mod.Sha}.part");
    await File.WriteAllBytesAsync(temp, updated[..3]);
    var rangeHandler = new FakeHandler(request =>
    {
        Check(request.Headers.Range?.Ranges.Single().From == 3, "resume requests only the missing bytes");
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(updated[3..]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, updated.Length - 1, updated.Length);
        return response;
    });
    await new ModManagerService(new HttpClient(rangeHandler)).DownloadAllModsAsync([mod], resume, (_, _, _, _) => { });
    Check((await File.ReadAllBytesAsync(Path.Combine(resume, mod.Name))).SequenceEqual(updated), "version-specific partial file resumes correctly");

    string pair = Folder("pair");
    var sig = Mod("test.sig", updated);
    await File.WriteAllBytesAsync(Path.Combine(pair, mod.Name), old);
    await File.WriteAllBytesAsync(Path.Combine(pair, sig.Name), old);
    var pairService = new ModManagerService(new HttpClient(new FakeHandler(request => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig") ? old : updated) })));
    result = await pairService.DownloadAllModsAsync([mod, sig], pair, (_, _, _, _) => { });
    Check(result.errors == 1 && (await File.ReadAllBytesAsync(Path.Combine(pair, mod.Name))).SequenceEqual(old), "failed signature download prevents pak replacement");

    string cleanup = Folder("cleanup");
    var obsolete = Mod("obsolete.pak", updated);
    await service.DownloadAllModsAsync([obsolete], cleanup, (_, _, _, _) => { });
    await File.WriteAllBytesAsync(Path.Combine(cleanup, "personal.pak"), old);
    await service.RemoveObsoleteModsAsync([mod], ["skins"], cleanup);
    Check(File.Exists(Path.Combine(cleanup, obsolete.Name)), "unselected category is preserved");
    await service.RemoveObsoleteModsAsync([mod], ["core"], cleanup);
    Check(!File.Exists(Path.Combine(cleanup, obsolete.Name)) && File.Exists(Path.Combine(cleanup, "personal.pak")), "cleanup removes only tracked obsolete mods");

    string modified = Folder("modified");
    await service.DownloadAllModsAsync([obsolete], modified, (_, _, _, _) => { });
    await File.WriteAllBytesAsync(Path.Combine(modified, obsolete.Name), old);
    await service.RemoveObsoleteModsAsync([mod], ["core"], modified);
    Check(File.Exists(Path.Combine(modified, obsolete.Name)), "independently modified obsolete mod is preserved");
    bool rejected = false;
    try { await service.DownloadAllModsAsync([mod], root, (_, _, _, _) => { }); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "installation outside ~mods is rejected");
    var traversal = Mod("../escape.pak", updated);
    rejected = false;
    try { await service.DownloadAllModsAsync([traversal], Folder("traversal"), (_, _, _, _) => { }); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "path traversal is rejected");

    string ignored = Folder("range-ignored");
    await File.WriteAllBytesAsync(Path.Combine(ignored, $".amurka-{mod.Name}-{mod.Sha}.part"), updated[..3]);
    await service.DownloadAllModsAsync([mod], ignored, (_, _, _, _) => { });
    Check((await File.ReadAllBytesAsync(Path.Combine(ignored, mod.Name))).SequenceEqual(updated), "HTTP 200 restarts partial download instead of appending");

    string rollback = Folder("rollback");
    await File.WriteAllBytesAsync(Path.Combine(rollback, mod.Name), old);
    await File.WriteAllBytesAsync(Path.Combine(rollback, sig.Name), old);
    bool failed = false;
    using (var locked = new FileStream(Path.Combine(rollback, sig.Name), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { await service.DownloadAllModsAsync([mod, sig], rollback, (_, _, _, _) => { }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
    }
    Check(failed && (await File.ReadAllBytesAsync(Path.Combine(rollback, mod.Name))).SequenceEqual(old), "locked signature causes rollback of already replaced pak");

    string version = Folder("version");
    string oldPartial = Path.Combine(version, $".amurka-{mod.Name}-{Hash(old)}.part");
    await File.WriteAllBytesAsync(oldPartial, old[..3]);
    var versionService = new ModManagerService(new HttpClient(new FakeHandler(request =>
    {
        Check(request.Headers.Range == null, "partial file of another version is never resumed");
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(updated) };
    })));
    await versionService.DownloadAllModsAsync([mod], version, (_, _, _, _) => { });

    string migrated = Folder("migration");
    await File.WriteAllBytesAsync(Path.Combine(migrated, obsolete.Name), updated);
    await service.RefreshInstalledStateAsync([obsolete], migrated);
    service.RecordVerifiedInstalledMods([obsolete], migrated);
    Check((await service.FindObsoleteModsAsync([mod], migrated)).Count == 1, "verified existing files are tracked before the first update");

    var snapshotHandler = new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "raw.githubusercontent.com")
            return new(HttpStatusCode.NotFound);
        string json = request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? "{\"sha\":\"fixed-commit\"}"
            : "{\"truncated\":false,\"tree\":[{\"path\":\"core/test.pak\",\"type\":\"blob\",\"size\":8,\"sha\":\"" + Hash(updated) + "\"}]}";
        if (request.RequestUri.AbsolutePath.Contains("/trees/"))
            Check(request.RequestUri.AbsolutePath.EndsWith("fixed-commit"), "catalog is pinned to the resolved commit");
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    });
    var catalogService = new ModManagerService(new HttpClient(snapshotHandler));
    var catalog = await catalogService.FetchAvailableModsAsync(catalogService.GetDefaultCategories());
    Check(catalog.Single().DownloadUrl.Contains("/fixed-commit/core/test.pak"), "download URL uses the same immutable commit");

    string staticRevision = new string('a', 40);
    var staticHandler = new FakeHandler(request =>
    {
        Check(request.RequestUri!.Host == "raw.githubusercontent.com", "mod catalog avoids GitHub API entirely");
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"schemaVersion\":1,\"revision\":\"" + staticRevision +
            "\",\"files\":[{\"path\":\"core/test.pak\",\"size\":8,\"sha\":\"" + Hash(updated) + "\"}]}") };
    });
    var staticService = new ModManagerService(new HttpClient(staticHandler));
    var staticCatalog = await staticService.FetchAvailableModsAsync(staticService.GetDefaultCategories());
    Check(staticHandler.Count == 1 && staticCatalog.Single().DownloadUrl.Contains("/" + staticRevision + "/"), "static catalog preserves immutable download URLs");
    var noUpdateHandler = new FakeHandler(request =>
    {
        Check(request.RequestUri!.Host == "raw.githubusercontent.com", "up-to-date launcher does not consume API quota");
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"2.1.22\"}") };
    });
    var noUpdateClient = new HttpClient(noUpdateHandler);
    Check(await new UpdateService(noUpdateClient, noUpdateClient).CheckForUpdateAsync() == null && noUpdateHandler.Count == 1,
        "current static launcher metadata stops further checks");
    var newUpdateHandler = new FakeHandler(request =>
    {
        Check(request.RequestUri!.Host != "api.github.com", "available update is found without GitHub API");
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"2.1.24\",\"file\":\"AMURKA-PVE-Launcher-Setup-2.1.24.exe\",\"size\":100}") };
    });
    var newUpdateClient = new HttpClient(newUpdateHandler);
    Check((await new UpdateService(newUpdateClient, newUpdateClient).CheckForUpdateAsync())?.Version == "2.1.24" && newUpdateHandler.Count == 1,
        "new static launcher metadata supplies an update without API requests");

    if (args.Contains("--live"))
    {
        var live = new ModManagerService();
        var liveCatalog = await live.FetchAvailableModsAsync(live.GetDefaultCategories());
        var smallest = liveCatalog.MinBy(m => m.Size)!;
        string livePath = Folder("live");
        result = await live.DownloadAllModsAsync([smallest], livePath, (_, _, _, _) => { });
        Check(result.errors == 0 && smallest.IsInstalled, "live GitHub catalog and real mod content match");
        Console.WriteLine($"Live catalog: {liveCatalog.Count} files; verified {smallest.Category}/{smallest.Name}.");
    }
    if (args.Contains("--check-release"))
    {
        var updater = new UpdateService();
        var release = await updater.CheckForUpdateAsync();
        Check(updater.CurrentVersion == "2.1.23" && release?.Version == "2.1.24", "previous launcher detects published 2.1.24 update");
        Check(release!.DownloadUrl.EndsWith("/v2.1.24/AMURKA-PVE-Launcher-Setup-2.1.24.exe"), "updater resolves the published installer asset");
    }
    Console.WriteLine($"All {checks} checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Count++;
        return Task.FromResult(respond(request));
    }
}
