using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AmurkaLauncher.Models;
using AmurkaLauncher.Services;
using Microsoft.Win32;

namespace AmurkaLauncher;

public partial class MainWindow : Window
{
    private const string ServerIp = "85.88.179.207:7004";

    // Статичные замороженные кисти для исключения аллокаций и утечек памяти UI
    private static readonly SolidColorBrush ModeActiveForeground = CreateFrozenBrush(Color.FromRgb(10, 10, 10));
    private static readonly SolidColorBrush UpdateDotInactive = CreateFrozenBrush(Color.FromRgb(85, 85, 85));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly ConfigService _configService = new();
    private readonly ScumDetectionService _scumDetectionService = new();
    private readonly ModManagerService _modManagerService = new();
    private readonly GameLauncherService _gameLauncherService = new();
    private readonly DiscordRpcService _discordRpcService = new();
    private readonly UpdateService _updateService = new();
    private readonly LauncherVerificationService _verificationService = new();

    private LauncherConfig _config = new();
    private List<ModItem> _availableMods = [];
    private List<string> _installedMods = [];
    private List<ModItem> _notifyMods = [];
    private List<ModManagerService.ManagedMod> _obsoleteMods = [];

    private bool _isDownloading;
    private bool _isLoadingMods;
    private DateTime _lastModCheck = DateTime.MinValue;
    private UpdateInfo? _currentUpdate;
    private string? _downloadedInstallerPath;

    private readonly DispatcherTimer _updateCheckTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _verificationTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _updateCheckTimer.Interval = TimeSpan.FromSeconds(60);
        _updateCheckTimer.Tick += OnUpdateCheckTimerTick;

        _verificationTimer.Interval = TimeSpan.FromMinutes(2);
        _verificationTimer.Tick += OnVerificationTimerTick;

        _toastTimer.Interval = TimeSpan.FromSeconds(3.5);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
    }

    private async void OnUpdateCheckTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync();
            if (!_isDownloading && !_isLoadingMods && DateTime.UtcNow - _lastModCheck > TimeSpan.FromMinutes(5))
                await LoadModsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateTimer] Error: {ex.Message}");
        }
    }

    private async void OnVerificationTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await PingVerificationAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VerificationTimer] Error: {ex.Message}");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _discordRpcService.Initialize();
        AppVersionText.Text = $" · v{_updateService.CurrentVersion}";

        _config = _configService.LoadConfig();

        // Автопоиск игры, если путь не задан или неверен
        string? exe = _scumDetectionService.FindScumExe(_config.GamePath);
        if (exe == null)
        {
            string? detected = _scumDetectionService.AutoDetectScum();
            if (!string.IsNullOrEmpty(detected))
            {
                _config.GamePath = detected;
            }
        }

        // Автоопределение папки модов строго по правилу SteamLibrary\...\SCUM\SCUM\Content\Paks\~mods
        if (string.IsNullOrWhiteSpace(_config.ModsPath) && !string.IsNullOrWhiteSpace(_config.GamePath))
        {
            _config.ModsPath = _scumDetectionService.GetModsPath(_config.GamePath);
        }

        UpdatePathsUi();
        UpdateModeButtonsUi();

        SaveCurrentConfig();

        await LoadModsAsync();

        await CheckForUpdatesAsync();
        _updateCheckTimer.Start();

        _ = PingVerificationAsync();
        _verificationTimer.Start();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _updateCheckTimer.Stop();
        _verificationTimer.Stop();
        _toastTimer.Stop();
        _discordRpcService.Dispose();
    }

    private void SaveCurrentConfig()
    {
        _configService.SaveConfig(_config);
    }

    private void UpdatePathsUi()
    {
        GamePathInput.Text = _config.GamePath;
        ModsPathInput.Text = _config.ModsPath;

        string? exe = _scumDetectionService.FindScumExe(_config.GamePath);
        bool found = exe != null;

        GameStatusDot.Fill = (Brush)FindResource(found ? "AccentBrush" : "DangerBrush");
        GameStatusText.Text = found
            ? "SCUM найден"
            : (string.IsNullOrWhiteSpace(_config.GamePath) ? "Укажите путь к SCUM" : "SCUM не найден");
    }

    private void UpdateModeButtonsUi()
    {
        string mode = _config.LaunchMode.ToLowerInvariant();
        ApplyModeBtnStyle(ModeDx11Btn, mode == "dx11");
        ApplyModeBtnStyle(ModeDx12Btn, mode == "dx12");
        ApplyModeBtnStyle(ModeVulkanBtn, mode == "vulkan");
    }

    private void ApplyModeBtnStyle(Button btn, bool isActive)
    {
        btn.Background = isActive ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        btn.Foreground = isActive ? ModeActiveForeground : (Brush)FindResource("TextSecondaryBrush");
        btn.FontWeight = FontWeights.Bold;
        btn.FontSize = 11;
        btn.BorderThickness = new Thickness(0);
        btn.Padding = new Thickness(0, 6, 0, 6);
        btn.Cursor = Cursors.Hand;
    }

    private async Task<bool> LoadModsAsync()
    {
        if (_isLoadingMods || _isDownloading) return false;
        if (string.IsNullOrWhiteSpace(_config.ModsPath))
        {
            SetStatus("Настройте пути установки", false);
            return false;
        }

        _isLoadingMods = true;
        SetModControlsEnabled(false);
        DownloadBtn.IsEnabled = false;
        ModsNotifyDownloadBtn.IsEnabled = false;
        SetStatus("Проверка версий модов...");
        ProgressSection.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        ProgressLabel.Text = "Подключение к репозиторию модов...";

        try
        {
            _availableMods = await _modManagerService.FetchAvailableModsAsync();
            await _modManagerService.RefreshInstalledStateAsync(_availableMods, _config.ModsPath);
            _modManagerService.RecordVerifiedInstalledMods(_availableMods, _config.ModsPath);
            _obsoleteMods = await _modManagerService.FindObsoleteModsAsync(_availableMods, _config.ModsPath);
            _lastModCheck = DateTime.UtcNow;
            _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);

            UpdateCounts();
            RefreshNotifyBanner();
            SetStatus($"Доступно модов: {_availableMods.Count}");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка: {ex.Message}", false);
            ShowToast("Не удалось загрузить список модов", isError: true);
            return false;
        }
        finally
        {
            _isLoadingMods = false;
            SetModControlsEnabled(true);
            DownloadBtn.IsEnabled = true;
            ModsNotifyDownloadBtn.IsEnabled = true;
            ProgressSection.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCounts()
    {
        AvailableCountText.Text = _availableMods.Count.ToString();
        InstalledCountText.Text = _installedMods.Count.ToString();
    }

    private void RefreshNotifyBanner()
    {
        if (_availableMods.Count == 0) return;

        var installedSet = new HashSet<string>(_installedMods, StringComparer.OrdinalIgnoreCase);
        _notifyMods = _availableMods.Where(m => !m.IsInstalled || !installedSet.Contains(m.Name)).ToList();
        int obsoleteCount = _obsoleteMods.Count;

        if (_notifyMods.Count == 0 && obsoleteCount == 0)
        {
            ModsNotifyBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (_notifyMods.Count > 0) parts.Add($"Новых/обновленных: {_notifyMods.Count}");
        if (obsoleteCount > 0) parts.Add($"Устаревших: {obsoleteCount}");

        ModsNotifyTitle.Text = $"Нужна синхронизация: {_notifyMods.Count + obsoleteCount}";
        ModsNotifyDesc.Text = string.Join(" · ", parts);
        ModsNotifyBanner.Visibility = Visibility.Visible;
    }

    private void SetStatus(string text, bool ok = true)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(ok ? "AccentBrush" : "DangerBrush");
    }

    private void ShowToast(string message, bool isError = false)
    {
        ToastText.Text = message;
        ToastText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
        ToastBorder.BorderBrush = (Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async Task DownloadModsListAsync(List<ModItem> toDownload)
    {
        if (_isDownloading || _isLoadingMods) return;

        if (toDownload.Count == 0)
        {
            ShowToast("Нет модов для скачивания", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ModsPath))
        {
            ShowToast("Укажите папку модов", isError: true);
            return;
        }

        _isDownloading = true;
        SetModControlsEnabled(false);
        LaunchBtn.IsEnabled = false;
        DownloadBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
        ModsNotifyDownloadBtn.IsEnabled = false;
        ProgressSection.Visibility = Visibility.Visible;

        try
        {
            if (IsGameRunning())
                throw new InvalidOperationException("Закройте SCUM перед обновлением модов.");
            var (success, errors) = await _modManagerService.DownloadAllModsAsync(
                toDownload,
                _config.ModsPath,
                (percent, file, downloaded, total) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        DownloadProgressBar.Value = percent;
                        string sizeStr = total > 0 ? $"{FormatSize(downloaded)} / {FormatSize(total)}" : $"{percent}%";
                        ProgressLabel.Text = $"{file} — {sizeStr}";
                        SetStatus($"📥 {file} ({percent}%)");
                    });
                });

            if (errors == 0)
                await _modManagerService.RemoveObsoleteModsAsync(_availableMods, _config.ModsPath);
            await _modManagerService.RefreshInstalledStateAsync(_availableMods, _config.ModsPath);
            _obsoleteMods = await _modManagerService.FindObsoleteModsAsync(_availableMods, _config.ModsPath);

            _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);
            UpdateCounts();
            RefreshNotifyBanner();

            if (errors > 0)
            {
                ShowToast($"Не удалось обновить моды. Ошибок: {errors}. Повторите загрузку.", isError: true);
                SetStatus("Обновление модов не завершено", false);
            }
            else if (success > 0)
            {
                ShowToast($"✅ Скачано: {success}, ошибок: {errors}");
                SetStatus($"Скачано модов: {success}");
            }
            else
            {
                ShowToast("Все моды уже установлены");
                SetStatus("Все моды уже установлены");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка: {ex.Message}", isError: true);
            SetStatus("Ошибка скачивания", false);
        }
        finally
        {
            ProgressSection.Visibility = Visibility.Collapsed;
            LaunchBtn.IsEnabled = true;
            DownloadBtn.IsEnabled = true;
            DeleteBtn.IsEnabled = true;
            ModsNotifyDownloadBtn.IsEnabled = true;
            _isDownloading = false;
            SetModControlsEnabled(true);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F1} {units[i]}";
    }

    // --- Button Event Handlers ---

    private static bool IsGameRunning()
    {
        var processes = Process.GetProcesses();
        try { return processes.Any(p => p.ProcessName.Equals("SCUM", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.StartsWith("SCUM-Win64-Shipping", StringComparison.OrdinalIgnoreCase)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void SetModControlsEnabled(bool enabled)
    {
        GamePathInput.IsEnabled = enabled;
        ModsPathInput.IsEnabled = enabled;
        BrowseGameBtn.IsEnabled = enabled;
        BrowseModsBtn.IsEnabled = enabled;
        DeleteBtn.IsEnabled = enabled;
        LaunchBtn.IsEnabled = enabled;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ServerInfo_Click(object sender, MouseButtonEventArgs e)
    {
        Clipboard.SetText(ServerIp);
        ShowToast($"IP скопирован: {ServerIp}");
    }

    private async Task PingVerificationAsync()
    {
        try
        {
            string? steamId = _verificationService.GetActiveSteamId64(_config.GamePath);
            if (!string.IsNullOrEmpty(steamId))
            {
                int installedCount = _installedMods?.Count ?? 0;
                string currentVer = _updateService.CurrentVersion;
                await _verificationService.SendVerificationToAllAsync(steamId, currentVer, installedCount, ServerIp);
            }
        }
        catch { }
    }

    private async void LaunchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isLoadingMods) return;
        if (!await LoadModsAsync()) return;
        if (ModsNotifyBanner.Visibility == Visibility.Visible)
        {
            ShowToast("Обновите выбранные моды перед запуском SCUM", isError: true);
            return;
        }
        string? exe = _scumDetectionService.FindScumExe(_config.GamePath);
        if (exe == null)
        {
            ShowToast("SCUM не найден! Укажите путь к игре.", isError: true);
            return;
        }

        try
        {
            SetStatus("🚀 Запуск SCUM...");

            // Фоновый пинг верификации лаунчера на сервер SSM (WebPanel локальный и боевой)
            try
            {
                await Task.WhenAny(
                    PingVerificationAsync(),
                    Task.Delay(2500)
                );
            }
            catch { }

            _gameLauncherService.LaunchGame(exe, _config.LaunchMode);
            SetStatus($"🚀 Запуск SCUM ({_config.LaunchMode.ToUpper()})...");
            ShowToast("SCUM запущен!");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка запуска: {ex.Message}", isError: true);
            SetStatus("Ошибка запуска", false);
        }
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isLoadingMods) return;
        if (!await LoadModsAsync()) return;
        await DownloadModsListAsync(_availableMods);
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isLoadingMods) return;
        _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);
        if (_installedMods.Count == 0)
        {
            ShowToast("Моды не установлены");
            return;
        }

        var result = MessageBox.Show(
            $"Удалить все {_installedMods.Count} модов из папки ~mods?",
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            int removed = _modManagerService.DeleteAllMods(_config.ModsPath);
            _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);
            UpdateCounts();
            RefreshNotifyBanner();

            ShowToast($"✅ Удалено модов: {removed}");
            SetStatus($"Удалено модов: {removed}");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка удаления: {ex.Message}", isError: true);
        }
    }

    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
        {
            _config.LaunchMode = mode;
            UpdateModeButtonsUi();
            SaveCurrentConfig();
        }
    }

    private void BrowseGameBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку SCUM",
            InitialDirectory = Directory.Exists(_config.GamePath) ? _config.GamePath : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            _config.GamePath = dialog.FolderName;
            // Пересчитываем путь модов строго по правилу
            _config.ModsPath = _scumDetectionService.GetModsPath(_config.GamePath);
            UpdatePathsUi();
            SaveCurrentConfig();
            _ = LoadModsAsync();
        }
    }

    private void GamePathInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        string val = GamePathInput.Text.Trim();
        if (val == _config.GamePath) return;

        _config.GamePath = val;
        _config.ModsPath = _scumDetectionService.GetModsPath(val);
        UpdatePathsUi();
        SaveCurrentConfig();
    }

    private async void BrowseModsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку ~mods",
            InitialDirectory = Directory.Exists(_config.ModsPath) ? _config.ModsPath : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName.TrimEnd('\\', '/');
            if (!chosen.EndsWith("~mods", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.Combine(chosen, "~mods");
            }
            _config.ModsPath = chosen;
            ModsPathInput.Text = _config.ModsPath;
            SaveCurrentConfig();
            await LoadModsAsync();
        }
    }

    private void ModsPathInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        string val = ModsPathInput.Text.Trim();
        if (val == _config.ModsPath) return;

        _config.ModsPath = val;
        SaveCurrentConfig();
        foreach (var mod in _availableMods) mod.IsInstalled = false;
        _obsoleteMods = [];
        _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);
        UpdateCounts();
        RefreshNotifyBanner();
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"Не удалось открыть ссылку: {ex.Message}", isError: true);
            }
        }
    }

    private async void ModsNotifyDownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isLoadingMods) return;
        if (!await LoadModsAsync()) return;
        await DownloadModsListAsync(_availableMods);
    }

    private void ModsNotifyLaterBtn_Click(object sender, RoutedEventArgs e)
    {
        ModsNotifyBanner.Visibility = Visibility.Collapsed;
    }

    // --- Update System Handlers ---

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update != null)
            {
                _currentUpdate = update;
                UpdateStatusDot.Fill = (Brush)FindResource("AccentBrush");
                UpdateStatusDot.ToolTip = $"Доступно обновление v{update.Version}";

                UpdateVersionText.Text = $"v{update.Version}";
                UpdateOverlay.Visibility = Visibility.Visible;
                UpdateDownloadBtn.Visibility = Visibility.Visible;
                UpdateInstallBtn.Visibility = Visibility.Collapsed;
                UpdateManualBtn.Visibility = Visibility.Collapsed;
                UpdateProgressPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateStatusDot.Fill = UpdateDotInactive;
                UpdateStatusDot.ToolTip = "Обновления: актуально";
            }
        }
        catch
        {
            UpdateStatusDot.Fill = (Brush)FindResource("DangerBrush");
            UpdateStatusDot.ToolTip = "Ошибка проверки обновлений";
        }
    }

    private async void UpdateDownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUpdate == null) return;

        UpdateDownloadBtn.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;

        try
        {
            _downloadedInstallerPath = await _updateService.DownloadUpdateAsync(_currentUpdate, pct =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateProgressBar.Value = pct;
                    UpdateProgressLabel.Text = $"{pct}%";
                });
            });

            UpdateDownloadBtn.Visibility = Visibility.Collapsed;
            UpdateInstallBtn.Visibility = Visibility.Visible;
            UpdateTitle.Text = "Обновление скачано";
            UpdateDesc.Text = "Нажмите «Установить», чтобы применить обновление";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            UpdateTitle.Text = "Ошибка загрузки";
            UpdateDesc.Text = ex.Message;
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateManualBtn.Visibility = Visibility.Visible;
            UpdateDownloadBtn.IsEnabled = true;
        }
    }

    private void UpdateInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_downloadedInstallerPath))
        {
            _updateService.InstallUpdate(_downloadedInstallerPath);
            Application.Current.Shutdown();
        }
    }

    private void UpdateManualBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUpdate != null && !string.IsNullOrEmpty(_currentUpdate.DownloadUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _currentUpdate.DownloadUrl,
                UseShellExecute = true
            });
        }
    }

    private void UpdateLaterBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
    }
}
