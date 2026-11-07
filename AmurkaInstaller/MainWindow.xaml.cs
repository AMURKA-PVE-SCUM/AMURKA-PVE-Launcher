using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace AmurkaInstaller;

public partial class MainWindow : Window
{
    private const string AppVersion = "2.1.26";
    private readonly bool _isUpdate;
    private readonly bool _isUninstall;
    private string _targetDir = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        _isUpdate = args.Any(a => a.Equals("/UPDATE", StringComparison.OrdinalIgnoreCase) || a.Equals("-update", StringComparison.OrdinalIgnoreCase));
        _isUninstall = args.Any(a => a.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase) || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                       || string.Equals(Process.GetCurrentProcess().ProcessName, "uninstall", StringComparison.OrdinalIgnoreCase);

        VersionBadgeText.Text = $"Версия: v{AppVersion}";

        if (_isUninstall)
        {
            WelcomeView.Visibility = Visibility.Collapsed;
            UninstallView.Visibility = Visibility.Visible;
            return;
        }

        // Определение пути установки
        string defaultPath = DetermineDefaultInstallPath();
        InstallPathInput.Text = defaultPath;
        UpdateDiskSpace(defaultPath);

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isUpdate)
        {
            // Режим автообновления: ждем завершения предыдущего процесса и обновляем без лишних кликов
            await Task.Delay(400);
            await WaitForLauncherExitAsync();
            await RunInstallationAsync();
        }
    }

    private string DetermineDefaultInstallPath()
    {
        // 1. Проверяем, есть ли уже установленный путь в реестре
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\AMURKA PVE Launcher");
            if (key?.GetValue("InstallLocation") is string regPath && !string.IsNullOrWhiteSpace(regPath))
            {
                if (Directory.Exists(regPath) || _isUpdate)
                {
                    return regPath;
                }
            }
        }
        catch { }

        // 2. Для первой чистой установки: безопасный диск с играми (D:\, E:\)
        // НИ В КОЕМ СЛУЧАЕ НЕ C:\Windows и НЕ C:\Program Files во избежание проблем с UAC при загрузке модов!
        if (Directory.Exists(@"D:\"))
        {
            return @"D:\Games\AMURKA PVE Launcher";
        }
        if (Directory.Exists(@"E:\"))
        {
            return @"E:\Games\AMURKA PVE Launcher";
        }

        // 3. Безопасный изолированный каталог пользователя
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "AMURKA PVE Launcher");
    }

    private void UpdateDiskSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var drive = new DriveInfo(root);
                double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                DiskSpaceInfoText.Text = $"Диск {drive.Name.TrimEnd('\\')}: свободно {freeGb:F1} ГБ · Требуется для установки: ~180 МБ";
                return;
            }
        }
        catch { }

        DiskSpaceInfoText.Text = "Требуется свободного места: ~180 МБ";
    }

    private void InstallPathInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDiskSpace(InstallPathInput.Text);
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для установки AMURKA PVE Launcher",
            InitialDirectory = Directory.Exists(InstallPathInput.Text) ? InstallPathInput.Text : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName;
            if (!chosen.EndsWith("AMURKA PVE Launcher", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.Combine(chosen, "AMURKA PVE Launcher");
            }
            InstallPathInput.Text = chosen;
        }
    }

    private async void StartInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunInstallationAsync();
    }

    private async Task WaitForLauncherExitAsync()
    {
        for (int i = 0; i < 15; i++)
        {
            var processes = Process.GetProcessesByName("AMURKA PVE Launcher");
            if (processes.Length == 0)
                break;
            await Task.Delay(400);
        }
    }

    private async Task RunInstallationAsync()
    {
        _targetDir = InstallPathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(_targetDir))
        {
            MessageBox.Show("Пожалуйста, укажите корректную папку установки.", "AMURKA PVE Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WelcomeView.Visibility = Visibility.Collapsed;
        FinishedView.Visibility = Visibility.Collapsed;
        InstallingView.Visibility = Visibility.Visible;

        InstallTitleText.Text = _isUpdate ? "Обновление AMURKA PVE Launcher..." : "Установка AMURKA PVE Launcher...";
        SetProgress(0, "Подготовка к распаковке...");

        bool success = await Task.Run(() => ExtractAndSetup(_targetDir));

        if (!success)
        {
            MessageBox.Show("Не удалось распаковать файлы лаунчера. Проверьте права доступа к выбранной папке.", "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallingView.Visibility = Visibility.Collapsed;
            WelcomeView.Visibility = Visibility.Visible;
            return;
        }

        // Регистрация в реестре
        RegisterInWindows(_targetDir);

        // Создание ярлыков
        if (CreateDesktopShortcutCheck.IsChecked == true)
        {
            CreateDesktopShortcut(_targetDir);
        }
        if (CreateStartMenuShortcutCheck.IsChecked == true)
        {
            CreateStartMenuShortcuts(_targetDir);
        }

        if (_isUpdate)
        {
            // При автообновлении сразу перезапускаем лаунчер
            LaunchLauncher(_targetDir);
            Close();
            return;
        }

        // Показ финального экрана
        InstallingView.Visibility = Visibility.Collapsed;
        InstalledPathText.Text = _targetDir;
        FinishedView.Visibility = Visibility.Visible;
    }

    private bool ExtractAndSetup(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);

            // 1. Поиск источника файлов (встроенный ресурс payload.zip или соседний publish-standalone)
            Stream? zipStream = GetPayloadStream();
            if (zipStream != null)
            {
                using (zipStream)
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    int total = archive.Entries.Count;
                    int current = 0;

                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                        {
                            Directory.CreateDirectory(Path.Combine(targetDir, entry.FullName));
                            continue;
                        }

                        string destinationPath = Path.Combine(targetDir, entry.FullName);
                        string? dir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);
                        current++;

                        int percent = (int)((double)current / total * 100);
                        Dispatcher.Invoke(() => SetProgress(percent, $"Распаковка: {entry.Name}"));
                    }
                }
            }
            else
            {
                // Fallback для разработки: прямое копирование из publish-standalone
                string localDevDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "AmurkaLauncher", "publish-standalone");
                if (Directory.Exists(localDevDir))
                {
                    var files = Directory.GetFiles(localDevDir, "*.*", SearchOption.AllDirectories);
                    int total = files.Length;
                    int current = 0;

                    foreach (var file in files)
                    {
                        string relative = Path.GetRelativePath(localDevDir, file);
                        string destinationPath = Path.Combine(targetDir, relative);
                        string? dir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        File.Copy(file, destinationPath, true);
                        current++;
                        int percent = (int)((double)current / total * 100);
                        Dispatcher.Invoke(() => SetProgress(percent, $"Копирование: {Path.GetFileName(file)}"));
                    }
                }
                else
                {
                    return false;
                }
            }

            // Создаем копию установщика в папке как uninstall.exe
            try
            {
                string? currentExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                {
                    File.Copy(currentExe, Path.Combine(targetDir, "uninstall.exe"), true);
                }
            }
            catch { }

            Dispatcher.Invoke(() => SetProgress(100, "Файлы успешно установлены."));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Extract Error] {ex}");
            return false;
        }
    }

    private Stream? GetPayloadStream()
    {
        // 1. Попытка чтения встроенного ресурса
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
        if (resName != null)
        {
            return asm.GetManifestResourceStream(resName);
        }

        // 2. Попытка чтения соседнего файла payload.zip
        string adjacentZip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload.zip");
        if (File.Exists(adjacentZip))
        {
            return File.OpenRead(adjacentZip);
        }

        return null;
    }

    private void SetProgress(int percent, string status)
    {
        percent = Math.Clamp(percent, 0, 100);
        InstallPercentText.Text = $"{percent}%";
        InstallStatusText.Text = status;

        double maxWidth = ProgressBarTrack.ActualWidth;
        if (maxWidth <= 0) maxWidth = 520;
        ProgressBarIndicator.Width = maxWidth * (percent / 100.0);
    }

    private void RegisterInWindows(string installDir)
    {
        try
        {
            // HKCU\Software\AMURKA PVE Launcher
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\AMURKA PVE Launcher"))
            {
                key?.SetValue("InstallLocation", installDir);
            }

            // Регистрация в "Установка и удаление программ"
            string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher";
            using (var key = Registry.CurrentUser.CreateSubKey(uninstallKeyPath))
            {
                if (key != null)
                {
                    string exePath = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
                    string uninstallerPath = Path.Combine(installDir, "uninstall.exe");

                    key.SetValue("DisplayName", $"AMURKA PVE Launcher v{AppVersion}");
                    key.SetValue("DisplayIcon", $"{exePath},0");
                    key.SetValue("DisplayVersion", AppVersion);
                    key.SetValue("Publisher", "AMURKA PVE");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("UninstallString", $"\"{uninstallerPath}\"");
                    key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /UNINSTALL");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Registry Error] {ex.Message}");
        }
    }

    private void CreateDesktopShortcut(string installDir)
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktopDir, "AMURKA PVE Launcher.lnk");
            string targetExe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            CreateShortcut(shortcutPath, targetExe, "Запуск AMURKA PVE Launcher для SCUM");
        }
        catch { }
    }

    private void CreateStartMenuShortcuts(string installDir)
    {
        try
        {
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AMURKA PVE Launcher");
            Directory.CreateDirectory(startMenuDir);

            string targetExe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            string uninstallerExe = Path.Combine(installDir, "uninstall.exe");

            CreateShortcut(Path.Combine(startMenuDir, "AMURKA PVE Launcher.lnk"), targetExe, "AMURKA PVE Launcher");
            if (File.Exists(uninstallerExe))
            {
                CreateShortcut(Path.Combine(startMenuDir, "Удалить AMURKA PVE Launcher.lnk"), uninstallerExe, "Удалить AMURKA PVE Launcher");
            }
        }
        catch { }
    }

    private void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType != null)
        {
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell != null)
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Description = description;
                shortcut.IconLocation = targetPath + ",0";
                shortcut.Save();
            }
        }
    }

    private void LaunchAppBtn_Click(object sender, RoutedEventArgs e)
    {
        LaunchLauncher(_targetDir);
        Close();
    }

    private void LaunchLauncher(string installDir)
    {
        try
        {
            string exe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void ConfirmUninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 1. Удаляем ярлыки
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AMURKA PVE Launcher.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AMURKA PVE Launcher");
            if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, true);

            // 2. Удаляем реестр
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\AMURKA PVE Launcher", false);

            // 3. Отложенное удаление папки через cmd после выхода uninstall.exe
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak >nul & rd /s /q \"{currentDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            MessageBox.Show("AMURKA PVE Launcher успешно удален с вашего компьютера.", "AMURKA PVE Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при удалении: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
