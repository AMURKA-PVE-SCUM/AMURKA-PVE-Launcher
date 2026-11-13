using System.Security.Cryptography;
using System.Text;
using AmurkaLauncher.Services;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class UpdateSecurityTests
{
    [Fact]
    public void VerifySha512RejectsEmptyOrWhitespaceHashZeroTrust()
    {
        using var temp = new TempTestDir();
        string file = temp.CreateFile("setup.exe", "AMURKA-PAYLOAD"u8.ToArray());

        Assert.False(UpdateService.VerifySha512(file, string.Empty));
        Assert.False(UpdateService.VerifySha512(file, "   "));
        Assert.False(UpdateService.VerifySha512(file, null));
    }

    [Fact]
    public void VerifySha512RejectsMissingFileAndSizeMismatch()
    {
        using var temp = new TempTestDir();
        byte[] payload = Encoding.UTF8.GetBytes("valid-installer-content");
        string validHex = Convert.ToHexString(SHA512.HashData(payload));
        string file = temp.CreateFile("setup.exe", payload);
        string missing = Path.Combine(temp.RootPath, "missing.exe");

        Assert.False(UpdateService.VerifySha512(missing, validHex));
        Assert.False(UpdateService.VerifySha512(file, validHex, expectedSize: payload.Length + 1));
    }

    [Fact]
    public void VerifySha512AcceptsOnlyExactHashAndExactSize()
    {
        using var temp = new TempTestDir();
        byte[] payload = Encoding.UTF8.GetBytes("verified-binary-bytes");
        string validHex = Convert.ToHexString(SHA512.HashData(payload)).ToLowerInvariant();
        string file = temp.CreateFile("setup.exe", payload);

        Assert.True(UpdateService.VerifySha512(file, validHex, expectedSize: payload.Length));
        Assert.False(UpdateService.VerifySha512(file, new string('0', 128), expectedSize: payload.Length));
    }
}
