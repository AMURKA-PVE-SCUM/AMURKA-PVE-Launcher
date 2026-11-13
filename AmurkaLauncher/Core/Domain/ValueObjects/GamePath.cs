using System.IO;

namespace AmurkaLauncher.Core.Domain.ValueObjects;

public readonly record struct GamePath
{
    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    private GamePath(string value)
    {
        Value = value;
    }

    public static GamePath From(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return new GamePath(string.Empty);

        return new GamePath(Normalize(rawPath));
    }

    public static implicit operator string(GamePath path) => path.Value;

    public override string ToString() => Value;

    public bool DirectoryExists() => !IsEmpty && Directory.Exists(Value);

    public string? FindExecutable()
    {
        if (IsEmpty) return null;

        string[] possibleExePaths =
        [
            Path.Combine(Value, "SCUM", "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(Value, "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(Value, "SCUM.exe"),
            Path.Combine(Value, "SCUM", "Binaries", "Win64", "SCUM-Win64-Shipping.exe"),
            Path.Combine(Value, "Binaries", "Win64", "SCUM-Win64-Shipping.exe")
        ];

        foreach (var p in possibleExePaths)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Trim().TrimEnd('\\', '/');

        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetDirectoryName(normalized) ?? normalized;
        }

        if (normalized.EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/Binaries/Win64", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.Parent?.FullName;
            if (parent != null) normalized = parent;
        }
        else if (normalized.EndsWith(@"\Binaries", StringComparison.OrdinalIgnoreCase) ||
                 normalized.EndsWith("/Binaries", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }

        if (normalized.EndsWith(@"\~mods", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/~mods", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }

        if (normalized.EndsWith(@"\Content\Paks", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/Content/Paks", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.Parent?.FullName;
            if (parent != null) normalized = parent;
        }
        else if (normalized.EndsWith(@"\Content", StringComparison.OrdinalIgnoreCase) ||
                 normalized.EndsWith("/Content", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }

        if (normalized.EndsWith(@"\SCUM\SCUM", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/SCUM/SCUM", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }
        else
        {
            var parent = Directory.GetParent(normalized);
            if (parent != null && Path.GetFileName(normalized).Equals("SCUM", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(parent.FullName, "SCUM", "Binaries", "Win64", "SCUM.exe")))
                {
                    normalized = parent.FullName;
                }
            }
        }

        return normalized.TrimEnd('\\', '/');
    }
}
