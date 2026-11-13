namespace ModSync.Tests.TestHelpers;

internal sealed class TempTestDir : IDisposable
{
    public string RootPath { get; }

    public TempTestDir()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"amurka-xunit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string CreateSubFolder(string name)
    {
        string path = Path.Combine(RootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, byte[] content)
    {
        string full = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateFile(string relativePath, string content)
    {
        string full = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test directory
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
