using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amurka.Shared.Security;
using AmurkaLauncher.Models;
using AmurkaLauncher.Services;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class ModSyncEngineTests
{
    private static string GitBlobHash(byte[] data) => GitBlobHasher.ComputeBytesHash(data);

    private static string MakeModsPath(TempTestDir temp, string name)
    {
        string dir = Path.Combine(temp.RootPath, name, "~mods");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ComputeGitBlobHashMatchesStandardGitBlobSha1()
    {
        using var temp = new TempTestDir();
        byte[] payload = "Hello AMURKA PVE"u8.ToArray();
        string file = temp.CreateFile("test.pak", payload);

        string actual = await ModManagerService.ComputeGitBlobHashAsync(file);

        Assert.Equal(GitBlobHash(payload), actual);
    }

    [Fact]
    public async Task DownloadAllModsDownloadsVerifiesTracksStateAndCleansObsolete()
    {
        using var temp = new TempTestDir();
        string modsPath = MakeModsPath(temp, "install");

        byte[] pakV1 = Encoding.UTF8.GetBytes("pak-v1");
        byte[] sigV1 = Encoding.UTF8.GetBytes("sig-v1");
        byte[] pakV2 = Encoding.UTF8.GetBytes("pak-v2-updated");
        byte[] sigV2 = Encoding.UTF8.GetBytes("sig-v2-updated");

        string pakUrl = "https://mock.test/mod.pak";
        string sigUrl = "https://mock.test/mod.sig";

        using var handler = new FakeHandler(req =>
        {
            string url = req.RequestUri!.ToString();
            byte[] content = url == pakUrl ? pakV2 : sigV2;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        });
        using var client = new HttpClient(handler);
        using var svc = new ModManagerService(client);

        // Pre-seed v1 files
        File.WriteAllBytes(Path.Combine(modsPath, "AMURKA.pak"), pakV1);
        File.WriteAllBytes(Path.Combine(modsPath, "AMURKA.sig"), sigV1);
        File.WriteAllBytes(Path.Combine(modsPath, "obsolete.pak"), "old"u8.ToArray());

        var catalog = new List<ModItem>
        {
            new() { Name = "AMURKA.pak", Category = "Main", Size = pakV2.Length, Sha = GitBlobHash(pakV2), DownloadUrl = pakUrl },
            new() { Name = "AMURKA.sig", Category = "Main", Size = sigV2.Length, Sha = GitBlobHash(sigV2), DownloadUrl = sigUrl }
        };

        await svc.RefreshInstalledStateAsync(catalog, modsPath);
        Assert.All(catalog, m => Assert.False(m.IsInstalled));

        var (success, errors) = await svc.DownloadAllModsAsync(catalog, modsPath, (_, _, _, _) => { });
        Assert.Equal(2, success);
        Assert.Equal(0, errors);
        Assert.All(catalog, m => Assert.True(m.IsInstalled));
        Assert.Equal(pakV2, File.ReadAllBytes(Path.Combine(modsPath, "AMURKA.pak")));
        Assert.Equal(sigV2, File.ReadAllBytes(Path.Combine(modsPath, "AMURKA.sig")));
        Assert.True(File.Exists(Path.Combine(modsPath, ".amurka-installed.json")));

        int removed = await svc.RemoveObsoleteAndUntrackedModsAsync(catalog, modsPath);
        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(modsPath, "obsolete.pak")));

        // Second refresh hits fast-path without re-downloading
        await svc.RefreshInstalledStateAsync(catalog, modsPath);
        Assert.All(catalog, m => Assert.True(m.IsInstalled));
    }

    [Fact]
    public async Task AdoptsValidLocalModWithoutRedownloadingAndRejectsCorruptDownload()
    {
        using var temp = new TempTestDir();
        string modsPath = MakeModsPath(temp, "adopt");
        byte[] validBytes = "valid-local-pak"u8.ToArray();
        File.WriteAllBytes(Path.Combine(modsPath, "Valid.pak"), validBytes);

        var mod = new ModItem
        {
            Name = "Valid.pak",
            Category = "Main",
            Size = validBytes.Length,
            Sha = GitBlobHash(validBytes),
            DownloadUrl = "https://mock.test/Valid.pak"
        };

        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("corrupt-bytes!!"u8.ToArray())
        });
        using var client = new HttpClient(handler);
        using var svc = new ModManagerService(client);

        await svc.RefreshInstalledStateAsync([mod], modsPath);
        Assert.True(mod.IsInstalled);

        var (s1, e1) = await svc.DownloadAllModsAsync([mod], modsPath, (_, _, _, _) => { });
        Assert.Equal(1, s1);
        Assert.Equal(0, e1);
        Assert.Equal(0, handler.Count);

        var badMod = new ModItem
        {
            Name = "Corrupt.pak",
            Category = "Main",
            Size = 15,
            Sha = new string('a', 40),
            DownloadUrl = "https://mock.test/Corrupt.pak"
        };
        var (_, e2) = await svc.DownloadAllModsAsync([badMod], modsPath, (_, _, _, _) => { });
        Assert.Equal(1, e2);
        Assert.False(File.Exists(Path.Combine(modsPath, "Corrupt.pak")));
    }

    [Fact]
    public async Task SupportsHttpRangeResumeAndAtomicRollbackOnPairFailure()
    {
        using var temp = new TempTestDir();
        string modsPath = MakeModsPath(temp, "resume");

        byte[] fullPak = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        string sha = GitBlobHash(fullPak);
        string partialName = $".amurka-Resume.pak-{sha}.part";
        File.WriteAllBytes(Path.Combine(modsPath, partialName), fullPak[..6]);

        using var resumeHandler = new FakeHandler(req =>
        {
            long from = req.Headers.Range?.Ranges.First().From ?? 0;
            Assert.Equal(6, from);
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(fullPak[6..])
            };
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(6, fullPak.Length - 1, fullPak.Length);
            return resp;
        });
        using var resumeClient = new HttpClient(resumeHandler);
        using var resumeSvc = new ModManagerService(resumeClient);

        var mod = new ModItem { Name = "Resume.pak", Category = "Main", Size = fullPak.Length, Sha = sha, DownloadUrl = "https://mock.test/Resume.pak" };
        var (ok, err) = await resumeSvc.DownloadAllModsAsync([mod], modsPath, (_, _, _, _) => { });
        Assert.Equal(1, ok);
        Assert.Equal(0, err);
        Assert.Equal(fullPak, File.ReadAllBytes(Path.Combine(modsPath, "Resume.pak")));

        // Pair failure rolls back already staged mod
        string rollbackPath = MakeModsPath(temp, "rollback");
        byte[] origA = "origA"u8.ToArray();
        File.WriteAllBytes(Path.Combine(rollbackPath, "Pair.pak"), origA);

        byte[] nextA = "nextA"u8.ToArray();
        byte[] nextSig = "nextSig"u8.ToArray();
        using var failHandler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(nextA) };
        });
        using var failClient = new HttpClient(failHandler);
        using var failSvc = new ModManagerService(failClient);

        var pair = new List<ModItem>
        {
            new() { Name = "Pair.pak", Category = "Main", Size = nextA.Length, Sha = GitBlobHash(nextA), DownloadUrl = "https://mock.test/Pair.pak" },
            new() { Name = "Pair.sig", Category = "Main", Size = nextSig.Length, Sha = GitBlobHash(nextSig), DownloadUrl = "https://mock.test/Pair.sig" }
        };
        var (_, pairErr) = await failSvc.DownloadAllModsAsync(pair, rollbackPath, (_, _, _, _) => { });
        Assert.True(pairErr > 0);
        Assert.Equal(origA, File.ReadAllBytes(Path.Combine(rollbackPath, "Pair.pak")));
    }

    [Fact]
    public async Task RejectsPathTraversalAndNonModsDirectory()
    {
        using var temp = new TempTestDir();
        string notMods = Path.Combine(temp.RootPath, "regular-folder");
        Directory.CreateDirectory(notMods);

        using var svc = new ModManagerService();

        Assert.Throws<InvalidOperationException>(() => svc.DeleteAllMods(notMods));

        var traversalMod = new ModItem
        {
            Name = "../escape.pak",
            Category = "Main",
            Size = 4,
            Sha = GitBlobHash("evil"u8.ToArray()),
            DownloadUrl = "https://mock.test/evil.pak"
        };

        string validMods = MakeModsPath(temp, "traversal");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DownloadAllModsAsync([traversalMod], validMods, (_, _, _, _) => { }));
    }

    [Fact]
    public async Task FetchAvailableModsReadsStaticCatalogJsonWithoutApiRateLimits()
    {
        byte[] modBytes = "static-mod"u8.ToArray();
        string modSha = GitBlobHash(modBytes);
        string revision = new('1', 40);
        string catalogJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision,
            files = new[]
            {
                new { path = "core/AmurkaCore.pak", size = modBytes.Length, sha = modSha }
            }
        });

        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        using var svc = new ModManagerService(client);

        List<ModItem> items = await svc.FetchAvailableModsAsync([new ModCategory { Id = "core", Label = "Основные", IsSelected = true }]);
        ModItem item = Assert.Single(items);
        Assert.Equal("AmurkaCore.pak", item.Name);
        Assert.Equal(modSha, item.Sha);
        Assert.Contains("gitverse.ru", item.DownloadUrl, StringComparison.Ordinal);
        Assert.Contains("core", item.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task LiveYandexDiskCatalogCanBeFetchedAndParsed()
    {
        using var client = Amurka.Shared.Network.HttpClientDefaults.CreateClient(TimeSpan.FromSeconds(15));
        var svc = new ModManagerService(client);
        var mods = await svc.FetchAvailableModsAsync();
        Assert.NotEmpty(mods);
        Assert.Equal(59, mods.Count);
        Assert.Contains("downloader.disk.yandex.ru", mods[0].DownloadUrl, StringComparison.Ordinal);
    }
}
