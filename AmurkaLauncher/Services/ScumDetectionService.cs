using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AmurkaLauncher.Services;

public class ScumDetectionService
{
    public string? AutoDetectScum()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Steam из реестра
        string?[] regPaths =
        [
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Valve\Steam", "InstallPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath")
        ];

        foreach (var rp in regPaths)
        {
            if (!string.IsNullOrWhiteSpace(rp) && Directory.Exists(rp))
            {
                candidates.Add(Path.GetFullPath(rp));
            }
        }

        // 2) Стандартные пути
        string[] standardPaths =
        [
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        ];
        foreach (var p in standardPaths)
        {
            if (Directory.Exists(p))
            {
                candidates.Add(p);
            }
        }

        // 3) Поиск Steam по всем дискам D-Z
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType == DriveType.Fixed)
            {
                string root = drive.RootDirectory.FullName;
                string[] possibleDirs = ["Steam", "SteamLibrary"];
                foreach (var dir in possibleDirs)
                {
                    string candidate = Path.Combine(root, dir);
                    if (Directory.Exists(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        // 4) Все библиотеки из libraryfolders.vdf
        var baseList = candidates.ToList();
        foreach (var steamBase in baseList)
        {
            string vdf = Path.Combine(steamBase, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string content = File.ReadAllText(vdf);
                    var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                    foreach (Match m in matches)
                    {
                        if (m.Groups.Count > 1)
                        {
                            string libPath = m.Groups[1].Value.Replace(@"\\", @"\");
                            if (Directory.Exists(libPath))
                            {
                                candidates.Add(Path.GetFullPath(libPath));
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors
                }
            }
        }

        // 5) Быстрый поиск по appmanifest (SCUM AppID = 513710)
        foreach (var lib in candidates)
        {
            string manifest = Path.Combine(lib, "steamapps", "appmanifest_513710.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    string content = File.ReadAllText(manifest);
                    var match = Regex.Match(content, @"""installdir""\s+""([^""]+)""");
                    if (match.Success && match.Groups.Count > 1)
                    {
                        string installDir = match.Groups[1].Value;
                        string scum = Path.Combine(lib, "steamapps", "common", installDir);
                        if (FindScumExe(scum) != null)
                        {
                            return scum;
                        }
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        // 6) Перебор common/SCUM во всех библиотеках
        foreach (var lib in candidates)
        {
            string scum = Path.Combine(lib, "steamapps", "common", "SCUM");
            if (FindScumExe(scum) != null)
            {
                return scum;
            }
        }

        return null;
    }

    public string? FindScumExe(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;

        string normalized = gamePath.TrimEnd('\\', '/');

        string[] possibleExePaths =
        [
            Path.Combine(normalized, "SCUM", "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(normalized, "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(normalized, "SCUM.exe")
        ];

        foreach (var p in possibleExePaths)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public string GetModsPath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return string.Empty;

        string normalized = gamePath.TrimEnd('\\', '/');

        // Если пользователь указал папку SCUM\SCUM:
        if (normalized.EndsWith(@"\SCUM\SCUM", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/SCUM/SCUM", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(normalized, "Content", "Paks", "~mods");
        }

        // Если указана папка ...\Binaries\Win64
        if (normalized.EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/Binaries/Win64", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.Parent?.FullName;
            if (parent != null)
            {
                return Path.Combine(parent, "Content", "Paks", "~mods");
            }
        }

        // Для стандартного пути SteamLibrary\steamapps\common\SCUM:
        // Моды строго попадают в: SteamLibrary\steamapps\common\SCUM\SCUM\Content\Paks\~mods
        return Path.Combine(normalized, "SCUM", "Content", "Paks", "~mods");
    }

    private static string? GetRegistryValue(string fullKeyPath, string valueName)
    {
        try
        {
            string[] parts = fullKeyPath.Split('\\', 2);
            if (parts.Length < 2) return null;

            RegistryKey? baseKey = parts[0] switch
            {
                "HKEY_CURRENT_USER" => Registry.CurrentUser,
                "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                _ => null
            };

            if (baseKey == null) return null;

            using var subKey = baseKey.OpenSubKey(parts[1]);
            var val = subKey?.GetValue(valueName)?.ToString();
            return val?.Replace('/', '\\');
        }
        catch
        {
            return null;
        }
    }
}
