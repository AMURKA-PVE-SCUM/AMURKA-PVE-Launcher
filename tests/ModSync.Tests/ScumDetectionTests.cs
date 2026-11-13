using AmurkaLauncher.Services;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ScumDetectionTests
{
    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\SCUM\SCUM\Binaries\Win64\SCUM.exe", @"D:\SteamLibrary\steamapps\common\SCUM")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\SCUM\SCUM\Binaries\Win64", @"D:\SteamLibrary\steamapps\common\SCUM")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\SCUM\SCUM\Content\Paks\~mods", @"D:\SteamLibrary\steamapps\common\SCUM")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\SCUM\Content\Paks\~mods", @"D:\SteamLibrary\steamapps\common\SCUM")]
    [InlineData(@"D:\Games\SCUM\", @"D:\Games\SCUM")]
    public void NormalizeGameRootReturnsCanonicalScumDirectory(string rawInput, string expectedRoot)
    {
        Assert.Equal(expectedRoot, ScumDetectionService.NormalizeGameRoot(rawInput));
    }

    [Fact]
    public void ParseSteamLibrariesPrioritizesLibraryWithScumAppId513710()
    {
        using var temp = new TempTestDir();
        string lib1 = temp.CreateSubFolder("Lib1");
        string lib2 = temp.CreateSubFolder("Lib2");

        string vdf = $$"""
        "libraryfolders"
        {
            "0"
            {
                "path"      "{{lib1.Replace("\\", "\\\\")}}"
                "apps"
                {
                    "730"   "12345"
                }
            }
            "1"
            {
                "path"      "{{lib2.Replace("\\", "\\\\")}}"
                "apps"
                {
                    "513710"    "50000000"
                }
            }
        }
        """;

        List<string> libs = ScumDetectionService.ParseSteamLibraries(vdf);
        Assert.Equal(2, libs.Count);
        Assert.Equal(Path.GetFullPath(lib2), libs[0]);
        Assert.Equal(Path.GetFullPath(lib1), libs[1]);
    }

    [Fact]
    public void FindScumExeAndGetModsPathSupportStandardUnrealLayout()
    {
        using var temp = new TempTestDir();
        string gameRoot = temp.CreateSubFolder("SCUMGame");
        string exe = temp.CreateFile(@"SCUMGame\SCUM\Binaries\Win64\SCUM.exe", "MZ");
        temp.CreateSubFolder(@"SCUMGame\SCUM\Content\Paks");

        var svc = new ScumDetectionService();
        Assert.Equal(exe, svc.FindScumExe(gameRoot));
        Assert.Equal(Path.Combine(gameRoot, "SCUM", "Content", "Paks", "~mods"), svc.GetModsPath(gameRoot));
    }

    [Fact]
    public void ResolveAndEnsurePathsNormalizesAndCreatesModsDirectory()
    {
        using var temp = new TempTestDir();
        string gameRoot = temp.CreateSubFolder("SCUMMoved");
        temp.CreateFile(@"SCUMMoved\SCUM\Binaries\Win64\SCUM.exe", "MZ");
        temp.CreateSubFolder(@"SCUMMoved\SCUM\Content\Paks");

        var svc = new ScumDetectionService();
        string? gp = Path.Combine(gameRoot, "SCUM", "Binaries", "Win64");
        string? mp = @"Z:\NonExistent\OldDisk\~mods";

        bool changed = svc.ResolveAndEnsurePaths(ref gp, ref mp);

        Assert.True(changed);
        Assert.Equal(gameRoot, gp);
        Assert.Equal(Path.Combine(gameRoot, "SCUM", "Content", "Paks", "~mods"), mp);
        Assert.True(Directory.Exists(mp));
    }
}
