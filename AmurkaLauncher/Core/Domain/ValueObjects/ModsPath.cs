using System.IO;

namespace AmurkaLauncher.Core.Domain.ValueObjects;

public readonly record struct ModsPath
{
    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    private ModsPath(string value)
    {
        Value = value;
    }

    public static ModsPath From(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return new ModsPath(string.Empty);

        string cleaned = rawPath.Trim().TrimEnd('\\', '/');
        if (!cleaned.EndsWith("~mods", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = Path.Combine(cleaned, "~mods");
        }

        return new ModsPath(cleaned);
    }

    public static ModsPath FromGamePath(GamePath gamePath)
    {
        if (gamePath.IsEmpty) return new ModsPath(string.Empty);

        string root = gamePath.Value;

        // 1. Проверяем стандартную структуру Unreal Engine: root\SCUM\Content\Paks
        string nestedPaks = Path.Combine(root, "SCUM", "Content", "Paks");
        if (Directory.Exists(nestedPaks))
        {
            return new ModsPath(Path.Combine(nestedPaks, "~mods"));
        }

        // 2. Проверяем альтернативную/плоскую структуру: root\Content\Paks
        string directPaks = Path.Combine(root, "Content", "Paks");
        if (Directory.Exists(directPaks))
        {
            return new ModsPath(Path.Combine(directPaks, "~mods"));
        }

        // 3. Стандартный путь SCUM по умолчанию
        return new ModsPath(Path.Combine(root, "SCUM", "Content", "Paks", "~mods"));
    }

    public static implicit operator string(ModsPath path) => path.Value;

    public override string ToString() => Value;

    public bool DirectoryExists() => !IsEmpty && Directory.Exists(Value);

    public void EnsureCreated()
    {
        if (IsEmpty || Directory.Exists(Value)) return;

        try
        {
            Directory.CreateDirectory(Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ошибка создания папки будет выявлена при проверке прав записи/синхронизации
        }
    }
}
