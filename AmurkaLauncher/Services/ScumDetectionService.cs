using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AmurkaLauncher.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class ScumDetectionService
{
    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex InstallDirRegex();

    /// <summary>
    /// Автоматическое определение папки игры SCUM с поддержкой переноса на любой диск/SSD.
    /// 1. Проверяет активный запущенный процесс SCUM (мгновенное точное определение).
    /// 2. Ищет прямую запись Steam AppID 513710 в реестре Windows Uninstall (InstallLocation).
    /// 3. Ищет активный клиент Steam в реестре Windows.
    /// 4. Сканирует все подключенные диски (включая внешние SSD/HDD и нестандартные библиотеки).
    /// 5. Считывает libraryfolders.vdf с приоритетом библиотек, содержащих AppID 513710.
    /// 6. Проверяет appmanifest_513710.acf и стандартные пути common\SCUM.
    /// </summary>
    public string? AutoDetectScum()
    {
        // 1) Если игра прямо сейчас запущена — получаем реальный физический путь процесса
        string? running = DetectFromRunningProcess();
        if (!string.IsNullOrEmpty(running) && FindScumExe(running) != null)
        {
            return NormalizeGameRoot(running);
        }

        // 2) Прямая запись Steam App 513710 в реестре деинсталлятора Windows (InstallLocation)
        string?[] appRegLocations =
        [
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 513710", "InstallLocation"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 513710", "InstallLocation"),
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 513710", "InstallLocation")
        ];

        foreach (var loc in appRegLocations)
        {
            if (!string.IsNullOrWhiteSpace(loc) && FindScumExe(loc) != null)
            {
                return NormalizeGameRoot(loc);
            }
        }

        var candidateLibraries = new List<string>();
        var candidateSteamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 3) Steam из реестра
        string?[] steamRegPaths =
        [
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "SteamPath"),
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Valve\Steam", "InstallPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath")
        ];

        foreach (var rp in steamRegPaths)
        {
            if (!string.IsNullOrWhiteSpace(rp) && Directory.Exists(rp))
            {
                candidateSteamRoots.Add(Path.GetFullPath(rp));
            }
        }

        // 4) Стандартные системные пути Steam
        string[] standardPaths =
        [
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam"
        ];
        foreach (var p in standardPaths)
        {
            if (Directory.Exists(p))
            {
                candidateSteamRoots.Add(Path.GetFullPath(p));
            }
        }

        // 5) Поиск папок Steam и прямого расположения SCUM по ВСЕМ дискам системы (Fixed и Removable)
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                {
                    string root = drive.RootDirectory.FullName;

                    // Прямая проверка частых мест установки SCUM на любом диске
                    string[] directScumPaths =
                    [
                        Path.Combine(root, "SteamLibrary", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "Steam", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "Games", "SCUM"),
                        Path.Combine(root, "SCUM"),
                        Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "Games", "Steam", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "SteamGames", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "SCUM"),
                        Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "SCUM")
                    ];

                    foreach (var directPath in directScumPaths)
                    {
                        if (FindScumExe(directPath) != null)
                        {
                            return NormalizeGameRoot(directPath);
                        }
                    }

                    // Обнаружение базовых каталогов Steam / SteamLibrary на диске
                    string[] possibleSteamDirs =
                    [
                        Path.Combine(root, "Steam"),
                        Path.Combine(root, "SteamLibrary"),
                        Path.Combine(root, "Games", "Steam"),
                        Path.Combine(root, "Games", "SteamLibrary"),
                        Path.Combine(root, "SteamGames")
                    ];

                    foreach (var sDir in possibleSteamDirs)
                    {
                        if (Directory.Exists(sDir))
                        {
                            candidateSteamRoots.Add(Path.GetFullPath(sDir));
                        }
                    }
                }
            }
            catch { }
        }

        // 6) Считывание libraryfolders.vdf из всех найденных каталогов Steam
        foreach (var steamBase in candidateSteamRoots)
        {
            string vdf = Path.Combine(steamBase, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string content = File.ReadAllText(vdf);
                    var parsedLibs = ParseSteamLibraries(content);
                    foreach (var lib in parsedLibs)
                    {
                        if (!candidateLibraries.Contains(lib, StringComparer.OrdinalIgnoreCase))
                        {
                            candidateLibraries.Add(lib);
                        }
                    }
                }
                catch { }
            }

            if (!candidateLibraries.Contains(steamBase, StringComparer.OrdinalIgnoreCase))
            {
                candidateLibraries.Add(steamBase);
            }
        }

        // 7) Точный поиск по appmanifest (SCUM AppID = 513710) в приоритетном порядке библиотек
        foreach (var lib in candidateLibraries)
        {
            string manifest = Path.Combine(lib, "steamapps", "appmanifest_513710.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    string content = File.ReadAllText(manifest);
                    var match = InstallDirRegex().Match(content);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        string installDir = match.Groups[1].Value;
                        string scum = Path.Combine(lib, "steamapps", "common", installDir);
                        if (FindScumExe(scum) != null)
                        {
                            return NormalizeGameRoot(scum);
                        }
                    }
                }
                catch { }
            }

            // Прямая проверка common\SCUM в этой библиотеке
            string directScum = Path.Combine(lib, "steamapps", "common", "SCUM");
            if (FindScumExe(directScum) != null)
            {
                return NormalizeGameRoot(directScum);
            }
        }

        return null;
    }

    /// <summary>
    /// Парсит содержимое libraryfolders.vdf.
    /// Библиотеки, в секции которых указан AppID SCUM (513710), помещаются в начало списка.
    /// </summary>
    public static List<string> ParseSteamLibraries(string vdfContent)
    {
        var prioritized = new List<string>();
        var others = new List<string>();

        if (string.IsNullOrWhiteSpace(vdfContent)) return prioritized;

        // Поиск блоков библиотек современного формата: "0" { "path" "..." ... "apps" { "513710" ... } }
        var libBlockRegex = new Regex(@"""\d+""\s*\{(?<content>[^{}]*?(?:\{[^{}]*\}[^{}]*?)*)\}", RegexOptions.Singleline);
        var matches = libBlockRegex.Matches(vdfContent);

        if (matches.Count > 0)
        {
            foreach (Match m in matches)
            {
                string block = m.Groups["content"].Value;
                var pathMatch = Regex.Match(block, @"""path""\s+""([^""]+)""");
                if (pathMatch.Success)
                {
                    string rawPath = pathMatch.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(rawPath))
                    {
                        string fullPath = Path.GetFullPath(rawPath);
                        if (block.Contains(@"""513710""", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!prioritized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                                prioritized.Add(fullPath);
                        }
                        else
                        {
                            if (!others.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                                others.Add(fullPath);
                        }
                    }
                }
            }
        }

        // Запасной вариант для старого формата VDF ("1" "D:\\SteamLibrary")
        var oldPathMatches = Regex.Matches(vdfContent, @"""(?:path|\d+)""\s+""([A-Za-z]:\\[^""]+)""");
        foreach (Match m in oldPathMatches)
        {
            string rawPath = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(rawPath))
            {
                string fullPath = Path.GetFullPath(rawPath);
                if (!prioritized.Contains(fullPath, StringComparer.OrdinalIgnoreCase) &&
                    !others.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    others.Add(fullPath);
                }
            }
        }

        prioritized.AddRange(others);
        return prioritized;
    }

    private static string? DetectFromRunningProcess()
    {
        try
        {
            string[] names = ["SCUM", "SCUM-Win64-Shipping"];
            foreach (var name in names)
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (var proc in processes)
                    {
                        try
                        {
                            string? exePath = proc.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                            {
                                return NormalizeGameRoot(Path.GetDirectoryName(exePath) ?? string.Empty);
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (var p in processes) p.Dispose();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Автоматическая сверка и гарантированная синхронизация путей игры и модов.
    /// Если игра была перенесена на другой диск — автоматически находит её и перепривязывает папку ~mods.
    /// </summary>
    public bool ResolveAndEnsurePaths(ref string? gamePath, ref string? modsPath)
    {
        bool changed = false;

        // 1. Проверяем, существует ли exe по текущему пути
        string? currentExe = FindScumExe(gamePath);

        // 2. Если exe не существует (игра перенесена на другой диск или путь пуст)
        if (currentExe == null)
        {
            string? detected = AutoDetectScum();
            if (!string.IsNullOrEmpty(detected))
            {
                gamePath = detected;
                changed = true;
            }
        }
        else
        {
            string normalized = NormalizeGameRoot(gamePath!);
            if (!string.Equals(gamePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                gamePath = normalized;
                changed = true;
            }
        }

        // 3. Автоматически перенаправляем папку ~mods на актуальный диск игры
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            string expectedMods = GetModsPath(gamePath);
            if (!string.Equals(modsPath, expectedMods, StringComparison.OrdinalIgnoreCase))
            {
                modsPath = expectedMods;
                changed = true;
            }

            // Гарантируем физическое создание папки ~mods
            try
            {
                if (!string.IsNullOrWhiteSpace(modsPath) && !Directory.Exists(modsPath))
                {
                    Directory.CreateDirectory(modsPath);
                }
            }
            catch { }
        }

        return changed;
    }

    /// <summary>
    /// Нормализует путь к корневому каталогу игры SCUM.
    /// Если пользователь выбрал SCUM.exe, Binaries\Win64, Content\Paks, ~mods или вложенную SCUM,
    /// метод корректно поднимается до канонического корня steamapps\common\SCUM.
    /// </summary>
    public static string NormalizeGameRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Trim().TrimEnd('\\', '/');

        // Если указан сам исполняемый файл
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetDirectoryName(normalized) ?? normalized;
        }

        // Если указана папка Binaries\Win64
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

        // Если указана папка ~mods
        if (normalized.EndsWith(@"\~mods", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/~mods", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }

        // Если указана папка Content\Paks
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

        // Если указана вложенная папка SCUM (например: ...\common\SCUM\SCUM)
        if (normalized.EndsWith(@"\SCUM\SCUM", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/SCUM/SCUM", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized)?.FullName;
            if (parent != null) normalized = parent;
        }
        else
        {
            // Проверка случая, когда выбрана внутренняя папка SCUM, а в родителе есть SCUM\Binaries\Win64\SCUM.exe
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

    public string? FindScumExe(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;

        string normalized = NormalizeGameRoot(gamePath);

        string[] possibleExePaths =
        [
            Path.Combine(normalized, "SCUM", "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(normalized, "Binaries", "Win64", "SCUM.exe"),
            Path.Combine(normalized, "SCUM.exe"),
            Path.Combine(normalized, "SCUM", "Binaries", "Win64", "SCUM-Win64-Shipping.exe"),
            Path.Combine(normalized, "Binaries", "Win64", "SCUM-Win64-Shipping.exe")
        ];

        foreach (var p in possibleExePaths)
        {
            if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>
    /// Возвращает точный физический путь к папке ~mods для указанного корня игры.
    /// Учитывает Unreal Engine структуру SCUM\Content\Paks\~mods и Content\Paks\~mods.
    /// </summary>
    public string GetModsPath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return string.Empty;

        string normalized = NormalizeGameRoot(gamePath);

        // 1. Проверяем стандартную структуру Unreal Engine: root\SCUM\Content\Paks
        string nestedPaks = Path.Combine(normalized, "SCUM", "Content", "Paks");
        if (Directory.Exists(nestedPaks))
        {
            return Path.Combine(nestedPaks, "~mods");
        }

        // 2. Проверяем альтернативную/плоскую структуру: root\Content\Paks
        string directPaks = Path.Combine(normalized, "Content", "Paks");
        if (Directory.Exists(directPaks))
        {
            return Path.Combine(directPaks, "~mods");
        }

        // 3. Стандартный путь SCUM по умолчанию
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
