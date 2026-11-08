using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AmurkaLauncher.Controls;
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
    private bool _isDownloadingUpdate;
    private bool _updateDismissed;
    private CancellationTokenSource? _updateCts;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == App.WmShowLauncher)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void OnUpdateCheckTimerTick(object? sender, EventArgs e)
    {
        if (_isDownloadingUpdate) return;
        try
        {
            await CheckForUpdatesAsync();
            if (!_isDownloading && !_isLoadingMods && !_isDownloadingUpdate && DateTime.UtcNow - _lastModCheck > TimeSpan.FromMinutes(5))
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
        // Неблокирующая фоновая инициализация Discord RPC
        _ = Task.Run(() => _discordRpcService.Initialize());
        AppVersionText.Text = _updateService.CurrentVersion;

        _config = _configService.LoadConfig();

        // Неблокирующий поиск и гарантированная привязка путей игры и модов (в фоновом пуле)
        string? gamePath = _config.GamePath;
        string? modsPath = _config.ModsPath;
        bool pathsChanged = await Task.Run(() => _scumDetectionService.ResolveAndEnsurePaths(ref gamePath, ref modsPath));
        _config.GamePath = gamePath ?? string.Empty;
        _config.ModsPath = modsPath ?? string.Empty;

        UpdatePathsUi();
        UpdateModeButtonsUi();

        if (pathsChanged)
        {
            SaveCurrentConfig();
        }

        // Параллельная загрузка списка модов и проверка обновлений для максимальной скорости старта
        await Task.WhenAll(
            LoadModsAsync(),
            CheckForUpdatesAsync()
        );

        _updateCheckTimer.Start();

        _ = PingVerificationAsync();
        _verificationTimer.Start();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
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
        if (GamePathInput.Text != _config.GamePath)
        {
            GamePathInput.Text = _config.GamePath;
        }

        if (ModsPathInput.Text != _config.ModsPath)
        {
            ModsPathInput.Text = _config.ModsPath;
        }

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
        int installedCount = _availableMods.Count(m => m.IsInstalled);
        InstalledCountText.Text = installedCount.ToString();
    }

    private void RefreshNotifyBanner()
    {
        if (_availableMods.Count == 0) return;

        var missingOrOutdated = _availableMods.Where(m => !m.IsInstalled).ToList();
        var untracked = _modManagerService.FindUntrackedOrObsoleteFiles(_availableMods, _config.ModsPath);
        var extraFileNames = new HashSet<string>(_obsoleteMods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var file in untracked) extraFileNames.Add(file);
        int extraCount = extraFileNames.Count;

        if (missingOrOutdated.Count == 0 && extraCount == 0)
        {
            ModsNotifyBanner.Visibility = Visibility.Collapsed;
            SetStatus("Моды синхронизированы с сервером", ok: true);
            return;
        }

        if (missingOrOutdated.Count > 0 && extraCount > 0)
        {
            ModsNotifyTitle.Text = "Требуется синхронизация модификаций";
            ModsNotifyDesc.Text = $"Скачать с сервера: {missingOrOutdated.Count} · Удалить сторонних: {extraCount}";
            SetStatus($"Требуется синхронизация ({missingOrOutdated.Count} из {_availableMods.Count})", ok: false);
        }
        else if (missingOrOutdated.Count > 0)
        {
            ModsNotifyTitle.Text = "Доступны обновления модификаций";
            ModsNotifyDesc.Text = $"Необходимо скачать: {missingOrOutdated.Count} из {_availableMods.Count} модов";
            SetStatus($"Требуется скачать: {missingOrOutdated.Count} из {_availableMods.Count}", ok: false);
        }
        else
        {
            ModsNotifyTitle.Text = "Обнаружены сторонние файлы";
            ModsNotifyDesc.Text = $"В папке найдено {extraCount} файлов не из репозитория GitHub";
            SetStatus($"Лишних файлов в папке: {extraCount}", ok: false);
        }

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

        if (string.IsNullOrWhiteSpace(_config.ModsPath))
        {
            ShowToast("Укажите папку модов", isError: true);
            return;
        }

        if (IsGameRunning())
        {
            AmurkaMessageBox.Show(
                "Игра SCUM сейчас запущена!\n\nСинхронизация модификаций невозможна во время работы игры, так как файлы игры заблокированы процессом SCUM.exe.\nПожалуйста, закройте игру SCUM и повторите попытку.",
                "Игра запущена",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Warning,
                this);
            ShowToast("Закройте SCUM перед синхронизацией модов", isError: true);
            return;
        }

        await _modManagerService.RefreshInstalledStateAsync(_availableMods, _config.ModsPath);
        var missingOrCorrupt = _availableMods.Where(m => !m.IsInstalled).ToList();
        var untracked = _modManagerService.FindUntrackedOrObsoleteFiles(_availableMods, _config.ModsPath);

        if (missingOrCorrupt.Count == 0 && untracked.Count == 0)
        {
            ShowToast("Моды полностью синхронизированы с GitHub");
            SetStatus("Моды полностью синхронизированы с GitHub");
            RefreshNotifyBanner();
            return;
        }

        _isDownloading = true;
        SetModControlsEnabled(false);
        LaunchBtn.IsEnabled = false;
        DownloadBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
        ModsNotifyDownloadBtn.IsEnabled = false;
        ProgressSection.Visibility = Visibility.Visible;

        int success = 0;
        int errors = 0;
        int removed = 0;

        try
        {
            if (missingOrCorrupt.Count > 0)
            {
                var downloadResult = await _modManagerService.DownloadAllModsAsync(
                    missingOrCorrupt,
                    _config.ModsPath,
                    (percent, file, downloaded, total) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            DownloadProgressBar.Value = percent;
                            string sizeStr = total > 0 ? $"{FormatSize(downloaded)} / {FormatSize(total)}" : $"{percent}%";
                            ProgressLabel.Text = $"{file} — {sizeStr}";
                            SetStatus($"Загрузка: {file} ({percent}%)");
                        });
                    });
                success = downloadResult.success;
                errors = downloadResult.errors;
            }

            if (errors == 0)
            {
                removed = await _modManagerService.RemoveObsoleteAndUntrackedModsAsync(_availableMods, _config.ModsPath);
            }

            await _modManagerService.RefreshInstalledStateAsync(_availableMods, _config.ModsPath);
            _modManagerService.RecordVerifiedInstalledMods(_availableMods, _config.ModsPath);
            _obsoleteMods = await _modManagerService.FindObsoleteModsAsync(_availableMods, _config.ModsPath);

            _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);
            UpdateCounts();
            RefreshNotifyBanner();

            if (errors > 0)
            {
                ShowToast($"Не удалось обновить моды. Ошибок: {errors}. Повторите загрузку.", isError: true);
                SetStatus("Обновление модов не завершено", false);
            }
            else if (success > 0 && removed > 0)
            {
                ShowToast($"Синхронизация завершена: скачано {success}, удалено лишних {removed}");
                SetStatus($"Синхронизировано (+{success}, -{removed})");
            }
            else if (success > 0)
            {
                ShowToast($"Синхронизация завершена: скачано {success}");
                SetStatus($"Скачано модов: {success}");
            }
            else if (removed > 0)
            {
                ShowToast($"Папка очищена: удалено лишних файлов: {removed}");
                SetStatus($"Удалено лишних файлов: {removed}");
            }
            else
            {
                ShowToast("Моды полностью синхронизированы с GitHub");
                SetStatus("Моды полностью синхронизированы с GitHub");
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
        string[] names = ["SCUM", "SCUM-Win64-Shipping", "SCUM_Launcher"];
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0) return true;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return false;
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
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (MaximizeIconPath != null)
                MaximizeIconPath.Data = (Geometry)FindResource("IconWinRestoreGeometry");
            if (MaximizeBtn != null)
                MaximizeBtn.ToolTip = "Восстановить";
        }
        else
        {
            if (MaximizeIconPath != null)
                MaximizeIconPath.Data = (Geometry)FindResource("IconWinMaximizeGeometry");
            if (MaximizeBtn != null)
                MaximizeBtn.ToolTip = "Развернуть";
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

    private async void WipeClientBtn_Click(object sender, RoutedEventArgs e)
    {
        bool confirm = AmurkaMessageBox.Show(
            "Полная очистка данных и кэша SCUM\n\n" +
            "• Процессы игры SCUM будут принудительно завершены (если запущены).\n" +
            "• Будет полностью удалена папка кэша и настроек:\n" +
            "   %LOCALAPPDATA%\\SCUM\n" +
            "• Это устраняет вылеты, сбои шейдеров и ошибки синхронизации серверов.\n" +
            "• При следующем запуске игра автоматически создаст чистые файлы.\n\n" +
            "Выполнить очистку прямо сейчас?",
            "Очистка клиента SCUM",
            AmurkaDialogButtons.YesNo,
            AmurkaDialogIcon.Warning,
            owner: this,
            confirmText: "Да, очистить",
            cancelText: "Отмена",
            isDanger: true
        ) == true;

        if (!confirm) return;

        WipeClientBtn.IsEnabled = false;
        SetStatus("Очистка клиента SCUM...");
        ShowToast("Выполняется очистка клиента...");

        try
        {
            var result = await Task.Run(() =>
            {
                // 1. Принудительное закрытие всех процессов SCUM
                string[] procNames = ["SCUM", "SCUM-Win64-Shipping", "SCUM_Launcher"];
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (procNames.Any(n => n.Equals(proc.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(3000);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }

                // 2. Очистка %LOCALAPPDATA%\SCUM
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string scumDir = Path.Combine(localAppData, "SCUM");

                if (!Directory.Exists(scumDir))
                {
                    return (success: true, wasEmpty: true, error: (string?)null);
                }

                try
                {
                    var dirInfo = new DirectoryInfo(scumDir);
                    foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            file.Attributes = FileAttributes.Normal;
                            file.Delete();
                        }
                        catch { }
                    }

                    foreach (var subDir in dirInfo.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
                    {
                        try { subDir.Delete(true); } catch { }
                    }

                    dirInfo.Delete(true);
                    return (success: !Directory.Exists(scumDir), wasEmpty: false, error: (string?)null);
                }
                catch (Exception ex)
                {
                    bool stillExists = Directory.Exists(scumDir);
                    return (success: !stillExists, wasEmpty: false, error: stillExists ? ex.Message : null);
                }
            });

            if (result.success)
            {
                string successMsg = result.wasEmpty
                    ? "Папка клиента SCUM уже была чиста!\n\nВременные файлы и кэш отсутствуют."
                    : "Очистка клиента SCUM успешно завершена!\n\nПапка %LOCALAPPDATA%\\SCUM полностью удалена.\nПри следующем входе игра создаст новые чистые файлы конфигурации.";

                AmurkaMessageBox.Show(
                    successMsg,
                    "Очистка завершена",
                    AmurkaDialogButtons.Ok,
                    AmurkaDialogIcon.Success,
                    owner: this);

                ShowToast("Клиент SCUM успешно очищен!");
                SetStatus("Клиент SCUM успешно очищен");
            }
            else
            {
                AmurkaMessageBox.Show(
                    "Часть файлов была удалена, однако некоторые файлы заблокированы Windows или другим процессом.\n\nРекомендуется перезагрузить ПК перед следующим запуском игры.",
                    "Внимание",
                    AmurkaDialogButtons.Ok,
                    AmurkaDialogIcon.Warning,
                    owner: this);

                ShowToast("Часть файлов заблокирована системой", isError: true);
                SetStatus("Очистка завершена с предупреждением", false);
            }
        }
        catch (Exception ex)
        {
            AmurkaMessageBox.Show(
                $"Ошибка при выполнении очистки:\n{ex.Message}",
                "Ошибка",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Error,
                owner: this);

            ShowToast($"Ошибка очистки: {ex.Message}", isError: true);
            SetStatus("Ошибка очистки", false);
        }
        finally
        {
            WipeClientBtn.IsEnabled = true;
        }
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

        if (IsGameRunning())
        {
            AmurkaMessageBox.Show(
                "Игра SCUM уже запущена на вашем компьютере!\n\nПереключитесь на уже открытое окно игры.",
                "Игра уже запущена",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Info,
                this);
            return;
        }

        if (!await LoadModsAsync()) return;

        // Полная проверка синхронизации модов перед запуском игры
        if (_availableMods.Count > 0)
        {
            var (isSynced, missingOrCorrupt, obsoleteFiles) = await _modManagerService.CheckSynchronizationAsync(_availableMods, _config.ModsPath);
            if (!isSynced)
            {
                var reasons = new List<string>();
                if (missingOrCorrupt.Count > 0)
                    reasons.Add($"Необходимо скачать или обновить: {missingOrCorrupt.Count} модов");
                if (obsoleteFiles.Count > 0)
                    reasons.Add($"Обнаружено устаревших/лишних файлов: {obsoleteFiles.Count}");

                string reasonText = string.Join("\n• ", reasons);

                bool? syncNow = AmurkaMessageBox.Show(
                    $"Внимание: обнаружен рассинхрон модификаций с сервером!\n\n• {reasonText}\n\nПеред подключением к серверу необходимо иметь 100% синхронизированный набор модов.\n\nВыполнить синхронизацию прямо сейчас?",
                    "Требуется синхронизация",
                    AmurkaDialogButtons.YesNo,
                    AmurkaDialogIcon.Warning,
                    this,
                    confirmText: "Синхронизировать",
                    cancelText: "Позже");

                if (syncNow == true)
                {
                    await DownloadModsListAsync(_availableMods);
                }
                return;
            }
        }

        string? gamePath = _config.GamePath;
        string? modsPath = _config.ModsPath;
        if (_scumDetectionService.ResolveAndEnsurePaths(ref gamePath, ref modsPath))
        {
            _config.GamePath = gamePath ?? string.Empty;
            _config.ModsPath = modsPath ?? string.Empty;
            UpdatePathsUi();
            SaveCurrentConfig();
        }

        string? exe = _scumDetectionService.FindScumExe(_config.GamePath);
        if (exe == null)
        {
            ShowToast("SCUM не найден! Укажите путь к игре.", isError: true);
            return;
        }

        try
        {
            SetStatus("Запуск SCUM...");

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
            SetStatus($"Запуск SCUM ({_config.LaunchMode.ToUpper()})...");
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

        if (IsGameRunning())
        {
            AmurkaMessageBox.Show(
                "Игра SCUM сейчас запущена!\n\nСкачивание и обновление модификаций невозможно во время работы игры, так как файлы игры заблокированы.\nПожалуйста, закройте игру SCUM и повторите попытку.",
                "Игра запущена",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Warning,
                this);
            ShowToast("Закройте SCUM перед скачиванием модов", isError: true);
            return;
        }

        string? gamePath = _config.GamePath;
        string? modsPath = _config.ModsPath;
        if (_scumDetectionService.ResolveAndEnsurePaths(ref gamePath, ref modsPath))
        {
            _config.GamePath = gamePath ?? string.Empty;
            _config.ModsPath = modsPath ?? string.Empty;
            UpdatePathsUi();
            SaveCurrentConfig();
        }

        if (!await LoadModsAsync()) return;
        await DownloadModsListAsync(_availableMods);
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isLoadingMods) return;

        if (IsGameRunning())
        {
            AmurkaMessageBox.Show(
                "Игра SCUM сейчас запущена!\n\nУдаление модификаций невозможно во время работы игры, так как файлы заблокированы процессом SCUM.exe.\nПожалуйста, закройте SCUM и повторите попытку.",
                "Игра запущена",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Warning,
                this);
            ShowToast("Закройте SCUM перед удалением модов", isError: true);
            return;
        }

        var filesOnDisk = Directory.Exists(_config.ModsPath)
            ? Directory.EnumerateFiles(_config.ModsPath, "*.*", SearchOption.TopDirectoryOnly).ToList()
            : [];
        if (filesOnDisk.Count == 0 && !_availableMods.Any(m => m.IsInstalled))
        {
            ShowToast("Папка модов уже пуста");
            return;
        }

        int deleteTargetCount = filesOnDisk.Count > 0 ? filesOnDisk.Count : _availableMods.Count(m => m.IsInstalled);

        bool? confirmed = AmurkaMessageBox.Show(
            $"Вы действительно хотите удалить все файлы ({deleteTargetCount}) из папки ~mods?\n\nЭто действие полностью очистит каталог модификаций.",
            "Подтверждение удаления",
            AmurkaDialogButtons.YesNo,
            AmurkaDialogIcon.Question,
            this,
            confirmText: "Удалить",
            cancelText: "Отмена",
            isDanger: true);

        if (confirmed != true) return;

        try
        {
            int removed = _modManagerService.DeleteAllMods(_config.ModsPath);
            foreach (var mod in _availableMods) mod.IsInstalled = false;
            await _modManagerService.RefreshInstalledStateAsync(_availableMods, _config.ModsPath);
            _obsoleteMods = await _modManagerService.FindObsoleteModsAsync(_availableMods, _config.ModsPath);
            _installedMods = _modManagerService.ScanInstalledMods(_config.ModsPath);

            UpdateCounts();
            RefreshNotifyBanner();

            ShowToast($"Удалено файлов: {removed}");
            SetStatus($"Удалено файлов: {removed}");
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
            string chosen = ScumDetectionService.NormalizeGameRoot(dialog.FolderName);
            _config.GamePath = chosen;
            _config.ModsPath = _scumDetectionService.GetModsPath(chosen);

            try
            {
                if (!string.IsNullOrWhiteSpace(_config.ModsPath) && !Directory.Exists(_config.ModsPath))
                {
                    Directory.CreateDirectory(_config.ModsPath);
                }
            }
            catch { }

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
        UpdateGameStatusOnly();
        SaveCurrentConfig();
    }

    private void GamePathInput_LostFocus(object sender, RoutedEventArgs e)
    {
        string val = GamePathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(val)) return;

        string normalized = ScumDetectionService.NormalizeGameRoot(val);
        if (!string.Equals(normalized, _config.GamePath, StringComparison.OrdinalIgnoreCase))
        {
            _config.GamePath = normalized;
            _config.ModsPath = _scumDetectionService.GetModsPath(normalized);

            try
            {
                if (!string.IsNullOrWhiteSpace(_config.ModsPath) && !Directory.Exists(_config.ModsPath))
                {
                    Directory.CreateDirectory(_config.ModsPath);
                }
            }
            catch { }

            UpdatePathsUi();
            SaveCurrentConfig();
            _ = LoadModsAsync();
        }
    }

    private void UpdateGameStatusOnly()
    {
        string? exe = _scumDetectionService.FindScumExe(_config.GamePath);
        bool found = exe != null;

        GameStatusDot.Fill = (Brush)FindResource(found ? "AccentBrush" : "DangerBrush");
        GameStatusText.Text = found
            ? "SCUM найден"
            : (string.IsNullOrWhiteSpace(_config.GamePath) ? "Укажите путь к SCUM" : "SCUM не найден");
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

            try
            {
                if (!Directory.Exists(chosen))
                {
                    Directory.CreateDirectory(chosen);
                }
            }
            catch { }

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

    private async void ModsPathInput_LostFocus(object sender, RoutedEventArgs e)
    {
        string val = ModsPathInput.Text.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(val)) return;

        if (!val.EndsWith("~mods", StringComparison.OrdinalIgnoreCase))
        {
            val = Path.Combine(val, "~mods");
        }

        try
        {
            if (!Directory.Exists(val))
            {
                Directory.CreateDirectory(val);
            }
        }
        catch { }

        if (val != _config.ModsPath)
        {
            _config.ModsPath = val;
            ModsPathInput.Text = val;
            SaveCurrentConfig();
            await LoadModsAsync();
        }
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

        if (IsGameRunning())
        {
            AmurkaMessageBox.Show(
                "Игра SCUM сейчас запущена!\n\nСкачивание и обновление модификаций невозможно во время работы игры, так как файлы игры заблокированы.\nПожалуйста, закройте игру SCUM и повторите попытку.",
                "Игра запущена",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Warning,
                this);
            ShowToast("Закройте SCUM перед скачиванием модов", isError: true);
            return;
        }

        string? gamePath = _config.GamePath;
        string? modsPath = _config.ModsPath;
        if (_scumDetectionService.ResolveAndEnsurePaths(ref gamePath, ref modsPath))
        {
            _config.GamePath = gamePath ?? string.Empty;
            _config.ModsPath = modsPath ?? string.Empty;
            UpdatePathsUi();
            SaveCurrentConfig();
        }

        if (!await LoadModsAsync()) return;
        await DownloadModsListAsync(_availableMods);
    }

    private void ModsNotifyLaterBtn_Click(object sender, RoutedEventArgs e)
    {
        ModsNotifyBanner.Visibility = Visibility.Collapsed;
    }

    // --- Update System Handlers ---

    private async void UpdateStatusDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentUpdate != null)
        {
            _updateDismissed = false;
            UpdateOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ShowToast("Проверка обновлений лаунчера...");
            await CheckForUpdatesAsync();
            if (_currentUpdate != null)
            {
                _updateDismissed = false;
                UpdateOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                ShowToast("У вас установлена актуальная версия!");
            }
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isDownloadingUpdate) return;
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (_isDownloadingUpdate) return;

            if (update != null)
            {
                _currentUpdate = update;
                UpdateStatusDot.Fill = (Brush)FindResource("AccentBrush");
                UpdateStatusDot.ToolTip = $"Доступно обновление v{update.Version}. Нажмите, чтобы открыть.";

                UpdateVersionText.Text = $"v{update.Version}";

                if (!string.IsNullOrEmpty(_downloadedInstallerPath) && File.Exists(_downloadedInstallerPath))
                {
                    UpdateDownloadBtn.Visibility = Visibility.Collapsed;
                    UpdateInstallBtn.Visibility = Visibility.Visible;
                    UpdateManualBtn.Visibility = Visibility.Collapsed;
                    UpdateProgressPanel.Visibility = Visibility.Collapsed;
                    UpdateTitle.Text = "Обновление готово к установке";
                    UpdateDesc.Text = "Нажмите «Установить», чтобы применить обновление.";
                    if (!_updateDismissed) UpdateOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    if (!_updateDismissed)
                    {
                        UpdateOverlay.Visibility = Visibility.Visible;
                        UpdateDownloadBtn.Visibility = Visibility.Visible;
                        UpdateInstallBtn.Visibility = Visibility.Collapsed;
                        UpdateManualBtn.Visibility = Visibility.Collapsed;
                        UpdateProgressPanel.Visibility = Visibility.Collapsed;
                    }
                }
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
        if (_currentUpdate == null || _isDownloadingUpdate) return;

        _isDownloadingUpdate = true;
        _updateCheckTimer.Stop();

        UpdateDownloadBtn.IsEnabled = false;
        UpdateDownloadBtn.Visibility = Visibility.Collapsed;
        UpdateManualBtn.Visibility = Visibility.Collapsed;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressLabel.Text = "Подготовка к загрузке...";
        UpdateTitle.Text = $"Загрузка обновления v{_currentUpdate.Version}";
        UpdateDesc.Text = "Пожалуйста, подождите завершения загрузки...";

        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();

        try
        {
            _downloadedInstallerPath = await _updateService.DownloadUpdateAsync(
                _currentUpdate,
                (pct, downloaded, total) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        UpdateProgressBar.Value = pct;
                        if (total > 0)
                        {
                            double mbDl = downloaded / (1024.0 * 1024.0);
                            double mbTot = total / (1024.0 * 1024.0);
                            UpdateProgressLabel.Text = $"{pct}% ({mbDl:F1} МБ / {mbTot:F1} МБ)";
                        }
                        else
                        {
                            UpdateProgressLabel.Text = $"{pct}%";
                        }
                    });
                },
                _updateCts.Token
            );

            UpdateDownloadBtn.Visibility = Visibility.Collapsed;
            UpdateInstallBtn.Visibility = Visibility.Visible;
            UpdateTitle.Text = "Обновление скачано";
            UpdateDesc.Text = "Нажмите «Установить», чтобы применить обновление.";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            UpdateTitle.Text = "Загрузка отменена";
            UpdateDesc.Text = "Вы можете повторить попытку в любое время.";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateDownloadBtn.IsEnabled = true;
            UpdateDownloadBtn.Visibility = Visibility.Visible;
            UpdateManualBtn.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateTitle.Text = "Ошибка загрузки";
            UpdateDesc.Text = $"Не удалось скачать обновление ({ex.Message}). Вы можете попробовать снова или скачать установщик вручную.";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateManualBtn.Visibility = Visibility.Visible;
            UpdateDownloadBtn.IsEnabled = true;
            UpdateDownloadBtn.Visibility = Visibility.Visible;
        }
        finally
        {
            _isDownloadingUpdate = false;
            _updateCheckTimer.Start();
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
        _updateDismissed = true;
        UpdateOverlay.Visibility = Visibility.Collapsed;
    }
}
