using Amurka.Shared.IO;
using ModSync.Tests.TestHelpers;
using Xunit;

namespace ModSync.Tests;

public sealed class AtomicFileStorageTests
{
    [Fact]
    public void WriteAllTextAtomicCreatesTargetWithoutLeftoverTempFiles()
    {
        using var temp = new TempTestDir();
        string target = Path.Combine(temp.RootPath, "nested", "config", "settings.json");

        AtomicFileStorage.WriteAllTextAtomic(target, "{\"version\":\"2.1.29\"}");

        Assert.True(File.Exists(target));
        Assert.Equal("{\"version\":\"2.1.29\"}", File.ReadAllText(target));

        string dir = Path.GetDirectoryName(target)!;
        string[] leftovers = Directory.GetFiles(dir, "*.tmp*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task WriteAllBytesAtomicAsyncOverwritesExistingContentSafely()
    {
        using var temp = new TempTestDir();
        string target = Path.Combine(temp.RootPath, "state.bin");

        byte[] initial = [1, 2, 3, 4];
        await AtomicFileStorage.WriteAllBytesAtomicAsync(target, initial);
        byte[] updated = [10, 20, 30, 40, 50, 60];
        await AtomicFileStorage.WriteAllBytesAtomicAsync(target, updated);

        Assert.Equal(updated, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp*"));
    }
}
