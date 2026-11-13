using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AmurkaLauncher.Models;
using AmurkaLauncher.Services;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("2.1.30", "2.1.29", true)]
    [InlineData("v2.2.0", "2.1.29", true)]
    [InlineData("2.1.29", "2.1.29", false)]
    [InlineData("2.1.28", "2.1.29", false)]
    [InlineData("2.10.0", "2.9.99", true)]
    public void IsNewerVersionComparesSemVerSegmentsCorrectly(string next, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(next, current));
    }

    [Fact]
    public async Task CheckForUpdateAsyncReadsRawLauncherJsonWhenNewerVersionExists()
    {
        const string launcherJson = """{"version":"2.1.35","file":"AMURKA-PVE-Launcher-Setup-2.1.35.exe","size":1024}""";
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(launcherJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client) { VersionOverride = "2.1.29" };

        UpdateInfo? info = await updater.CheckForUpdateAsync();

        Assert.NotNull(info);
        Assert.Equal("2.1.35", info.Version);
        Assert.Equal("AMURKA-PVE-Launcher-Setup-2.1.35.exe", info.File);
        Assert.EndsWith("/v2.1.35/AMURKA-PVE-Launcher-Setup-2.1.35.exe", info.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsyncReturnsNullWhenAlreadyOnLatestVersion()
    {
        const string launcherJson = """{"version":"2.1.29","file":"AMURKA-PVE-Launcher-Setup-2.1.29.exe","size":1024}""";
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(launcherJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client) { VersionOverride = "2.1.29" };

        UpdateInfo? info = await updater.CheckForUpdateAsync();
        Assert.Null(info);
    }

    [Fact]
    public async Task CheckForUpdateAsyncFallsBackToRedirectWhenRawJsonUnavailable()
    {
        using var rawFailHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var redirectHandler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/tag/v2.1.40");
            return resp;
        });

        using var rawClient = new HttpClient(rawFailHandler);
        using var redirectClient = new HttpClient(redirectHandler);
        var updater = new UpdateService(rawClient, redirectClient) { VersionOverride = "2.1.29" };

        UpdateInfo? info = await updater.CheckForUpdateAsync();
        Assert.NotNull(info);
        Assert.Equal("2.1.40", info.Version);
        Assert.Contains("AMURKA-PVE-Launcher-Setup-2.1.40.exe", info.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadUpdateAsyncSupportsPartialRangeResumeAndVerifiesSha512()
    {
        using var temp = new TempTestDir();
        byte[] installerBytes = Encoding.UTF8.GetBytes("MZ-AMURKA-FAKE-SETUP-BINARY-PAYLOAD-1234567890");
        string sha512 = Convert.ToHexString(SHA512.HashData(installerBytes));
        const string fileName = "AMURKA-PVE-Launcher-Setup-2.1.99.exe";

        // Seed partial temp file (first 10 bytes)
        File.WriteAllBytes(Path.Combine(temp.RootPath, fileName + ".tmp"), installerBytes[..10]);

        using var handler = new FakeHandler(req =>
        {
            long from = req.Headers.Range?.Ranges.First().From ?? 0;
            Assert.Equal(10, from);
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(installerBytes[10..])
            };
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, installerBytes.Length - 1, installerBytes.Length);
            return resp;
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client) { UpdatesDirectory = temp.RootPath };

        var info = new UpdateInfo
        {
            Version = "2.1.99",
            File = fileName,
            DownloadUrl = "https://mock.test/" + fileName,
            Size = installerBytes.Length,
            Sha512 = sha512
        };

        int lastPercent = 0;
        string path = await updater.DownloadUpdateAsync(info, (pct, _, _) => lastPercent = pct);

        Assert.Equal(100, lastPercent);
        Assert.True(File.Exists(path));
        Assert.Equal(installerBytes, File.ReadAllBytes(path));
    }
}
