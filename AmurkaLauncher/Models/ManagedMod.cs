namespace AmurkaLauncher.Models;

public sealed class ManagedMod
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public long Size { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }

    public ManagedMod() { }

    public ManagedMod(string name, string category, string sha, long size, long lastWriteTimeUtcTicks)
    {
        Name = name ?? string.Empty;
        Category = category ?? string.Empty;
        Sha = sha ?? string.Empty;
        Size = size;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
    }

    public static ManagedMod Create(string name, string category, string sha, long size, DateTime lastWriteTimeUtc) =>
        new(name, category, sha, size, lastWriteTimeUtc.Ticks);

    public bool MatchesSignature(long size, long lastWriteTimeTicks) =>
        Size == size && LastWriteTimeUtcTicks == lastWriteTimeTicks;

    public bool MatchesHash(string hash) =>
        string.Equals(Sha, hash, StringComparison.OrdinalIgnoreCase);
}
