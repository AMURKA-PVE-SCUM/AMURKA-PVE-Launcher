using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amurka.Shared.IO;
using Amurka.Shared.Security;

namespace Amurka.CatalogBuilder;

public static class Program
{
    private static readonly JsonSerializerOptions CachedJsonOptions = new() { WriteIndented = true };
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string rootDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("====================================================");
        Console.WriteLine("  AMURKA PVE: GitVerse Catalog Builder (v3.0.0)");
        Console.WriteLine("====================================================");
        Console.ResetColor();

        Console.WriteLine($"[INFO] Сканирование репозитория: {rootDir}");

        if (!Directory.Exists(rootDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Директория не найдена: {rootDir}");
            Console.ResetColor();
            return 1;
        }

        string[] searchCategories = ["core", "vehicles", "clothes", "weapons", "buildings", "music"];
        var filesList = new List<CatalogFileEntry>();

        foreach (string category in searchCategories)
        {
            string catDir = Path.Combine(rootDir, category);
            if (!Directory.Exists(catDir)) continue;

            Console.WriteLine($"[INFO] Сканирование категории: '{category}'");
            string[] foundFiles = Directory.GetFiles(catDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(PathSecurityGuard.HasModExtension)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string file in foundFiles)
            {
                string fileName = Path.GetFileName(file);
                var fi = new FileInfo(file);
                string gitBlobSha = GitBlobHasher.ComputeFileHashAsync(file).GetAwaiter().GetResult();

                Console.WriteLine($"  -> {category}/{fileName} ({fi.Length:N0} байт) [SHA: {gitBlobSha}]");
                filesList.Add(new CatalogFileEntry
                {
                    path = $"{category}/{fileName}",
                    size = fi.Length,
                    sha = gitBlobSha,
                    type = "blob"
                });
            }
        }

        if (filesList.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARN] Моды (.pak/.sig) не найдены в известных категориях.");
            Console.ResetColor();
        }

        string revision = ResolveCurrentGitRevision(rootDir);
        var catalog = new
        {
            schemaVersion = 1,
            revision,
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            files = filesList
        };

        string json = JsonSerializer.Serialize(catalog, CachedJsonOptions);
        string outputPath = Path.Combine(rootDir, "mods.json");
        File.WriteAllText(outputPath, json, Encoding.UTF8);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine($"[SUCCESS] Каталог успешно сформирован ({filesList.Count} файлов): {outputPath}");
        Console.WriteLine($"[SUCCESS] Git ревизия: {revision}");
        Console.ResetColor();

        return 0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Git revision SHA-1 specification")]
    private static string ResolveCurrentGitRevision(string repoRoot)
    {
        try
        {
            string headFile = Path.Combine(repoRoot, ".git", "HEAD");
            if (File.Exists(headFile))
            {
                string headContent = File.ReadAllText(headFile).Trim();
                if (headContent.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    string refPath = Path.Combine(repoRoot, ".git", headContent[5..].Trim().Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(refPath))
                    {
                        return File.ReadAllText(refPath).Trim().ToLowerInvariant();
                    }
                }
                else if (headContent.Length == 40 && headContent.All(Uri.IsHexDigit))
                {
                    return headContent.ToLowerInvariant();
                }
            }
        }
        catch { }

        // Fallback: 40-значный детерминированный SHA-1 от текущей метки времени
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();
    }

    private sealed class CatalogFileEntry
    {
        public string path { get; set; } = string.Empty;
        public long size { get; set; }
        public string sha { get; set; } = string.Empty;
        public string type { get; set; } = "blob";
    }
}
