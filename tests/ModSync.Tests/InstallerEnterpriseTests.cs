using System.IO.Compression;
using Amurka.Shared.Core.Domain.Results;
using Amurka.Shared.IO;
using Amurka.Shared.Resilience;
using AmurkaInstaller.Core.Domain.Enums;
using AmurkaInstaller.Core.Domain.Models;
using AmurkaInstaller.Infrastructure.Services;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class InstallerEnterpriseTests
{
    [Fact]
    public void PathSecurityGuardPreventsPrefixSiblingAndZipSlipTraversal()
    {
        using var temp = new TempTestDir();
        string root = temp.CreateSubFolder("App");
        string siblingBypass = Path.Combine(temp.RootPath, "AppEvil", "payload.dll");

        Assert.False(PathSecurityGuard.IsPathInsideRoot(root, siblingBypass));
        Assert.Null(PathSecurityGuard.ResolveSafeChildPath(root, "../../Windows/System32/cmd.exe"));
        Assert.Null(PathSecurityGuard.ResolveSafeChildPath(root, @"..\AppEvil\hack.exe"));

        string? safe = PathSecurityGuard.ResolveSafeChildPath(root, "sub/file.dll");
        Assert.NotNull(safe);
        Assert.True(PathSecurityGuard.IsPathInsideRoot(root, safe));
    }

    [Fact]
    public void ValidateInstallDirectoryRejectsRootsAndWindowsSystemDirectories()
    {
        Assert.True(PathSecurityGuard.ValidateInstallDirectory(string.Empty).IsFailure);
        Assert.True(PathSecurityGuard.ValidateInstallDirectory(@"C:\").IsFailure);
        Assert.True(PathSecurityGuard.ValidateInstallDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System)).IsFailure);

        Result<string> valid = PathSecurityGuard.ValidateInstallDirectory(@"D:\Games\AMURKA PVE Launcher\");
        Assert.True(valid.IsSuccess);
        Assert.Equal(@"D:\Games\AMURKA PVE Launcher", valid.Value);
    }

    [Fact]
    public void PayloadExtractorPreFlightChecksValidatePermissionsAndDiskSpace()
    {
        using var temp = new TempTestDir();
        var extractor = new PayloadExtractor();

        string installDir = Path.Combine(temp.RootPath, "InstallTarget");
        Assert.True(extractor.RunPreFlightChecks(installDir).IsSuccess);
        Assert.True(extractor.RunPreFlightChecks(installDir, requiredBytes: long.MaxValue / 2).IsFailure);
        Assert.True(extractor.RunPreFlightChecks(@"C:\").IsFailure);
    }

    [Fact]
    public void InstallerOptionsParsesCliFlagsAndRejectsUnsafeDirectory()
    {
        InstallerOptions safeOpts = InstallerOptions.Parse(["/UPDATE", @"/DIR=D:\Games\Amurka", "/NODESKTOP", "/NOSTARTMENU"]);
        Assert.Equal(InstallerMode.Update, safeOpts.Mode);
        Assert.Equal(@"D:\Games\Amurka", safeOpts.TargetPath);
        Assert.False(safeOpts.CreateDesktopShortcut);
        Assert.False(safeOpts.CreateStartMenuShortcut);

        InstallerOptions unsafeDirOpts = InstallerOptions.Parse([@"/DIR=C:\Windows\System32"]);
        Assert.Null(unsafeDirOpts.TargetPath);

        InstallerOptions uninstOpts = InstallerOptions.Parse([], processNameOverride: "uninstall");
        Assert.Equal(InstallerMode.Uninstall, uninstOpts.Mode);
    }

    [Fact]
    public async Task RetryPolicyRetriesTransientFailuresAndHonorsCancellation()
    {
        int attempts = 0;
        int result = await RetryPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 3) throw new IOException("Transient file lock");
            return Task.FromResult(42);
        }, maxAttempts: 4, baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(3, attempts);
        Assert.Equal(42, result);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryPolicy.ExecuteAsync(_ => Task.FromResult(1), maxAttempts: 3, ct: cts.Token));
    }

    [Fact]
    public void StreamTransferHelperCopiesAndCleansOldArtifacts()
    {
        using var temp = new TempTestDir();
        string dest = Path.Combine(temp.RootPath, "app.bin");
        byte[] content = "STREAM-CONTENT"u8.ToArray();

        StreamTransferHelper.CopyToFileWithRetry(() => new MemoryStream(content), dest, expectedLength: content.Length);
        Assert.Equal(content, File.ReadAllBytes(dest));

        File.WriteAllText(Path.Combine(temp.RootPath, "stale.old"), "old");
        StreamTransferHelper.CleanupOldFiles(temp.RootPath);
        Assert.False(File.Exists(Path.Combine(temp.RootPath, "stale.old")));
    }

    [Fact]
    public void MemoryOptimizerTrimWorkingSetExecutesSafely()
    {
        // Проверяем, что вызов уплотнения LOH и сброса страниц не вызывает падений
        var exception = Record.Exception(() => Amurka.Shared.Platform.MemoryOptimizer.TrimWorkingSet());
        Assert.Null(exception);
    }
}
