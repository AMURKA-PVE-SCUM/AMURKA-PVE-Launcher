using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Platform;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace AmurkaLauncher.Presentation.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class MainViewModel : ViewModelBase
{
    private static readonly string ServerIp = AppConstants.ServerAddress;

    private readonly IGameLauncherService _gameLauncherService;
    private readonly IClientMaintenanceService _maintenanceService;
    private readonly IDiscordRpcService _discordRpcService;
    private readonly IVerificationService _verificationService;
    private readonly IUpdateService _updateService;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly IConfigRepository _configRepository;
    private readonly ILogger<MainViewModel>? _logger;

    private readonly DispatcherTimer _updateCheckTimer = new();
    private readonly DispatcherTimer _verificationTimer = new();
    private readonly DispatcherTimer _toastTimer = new();

    public SettingsViewModel Settings { get; }
    public ModSyncViewModel ModSync { get; }
    public UpdateModalViewModel UpdateModal { get; }

    [ObservableProperty]
    private string _appVersionText = AppConstants.Version;

    [ObservableProperty]
    private string _statusText = "Готов к запуску";

    [ObservableProperty]
    private bool _isStatusOk = true;

    [ObservableProperty]
    private string _toastText = string.Empty;

    [ObservableProperty]
    private bool _isToastError;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDx11))]
    [NotifyPropertyChangedFor(nameof(IsDx12))]
    [NotifyPropertyChangedFor(nameof(IsVulkan))]
    private LaunchMode _launchMode = LaunchMode.Dx11;

    public bool IsDx11 => LaunchMode == LaunchMode.Dx11;
    public bool IsDx12 => LaunchMode == LaunchMode.Dx12;
    public bool IsVulkan => LaunchMode == LaunchMode.Vulkan;

    [ObservableProperty]
    private bool _isBusy;

    public event Func<string, string, bool>? ConfirmationRequested;
    public event Action<string, string>? MessageDialogRequested;
    public event Action? GameLaunchedSuccessfully;

    public MainViewModel(
        SettingsViewModel settings,
        ModSyncViewModel modSync,
        UpdateModalViewModel updateModal,
        IGameLauncherService gameLauncherService,
        IClientMaintenanceService maintenanceService,
        IDiscordRpcService discordRpcService,
        IVerificationService verificationService,
        IUpdateService updateService,
        IProcessSupervisor processSupervisor,
        IConfigRepository configRepository,
        ILogger<MainViewModel>? logger = null)
    {
        Settings = settings;
        ModSync = modSync;
        UpdateModal = updateModal;

        _gameLauncherService = gameLauncherService;
        _maintenanceService = maintenanceService;
        _discordRpcService = discordRpcService;
        _verificationService = verificationService;
        _updateService = updateService;
        _processSupervisor = processSupervisor;
        _configRepository = configRepository;
        _logger = logger;

        AppVersionText = _updateService.CurrentVersion;

        var cfg = _configRepository.LoadConfig();
        LaunchMode = LaunchModeExtensions.FromKey(cfg.LaunchMode);

        Settings.PathsChanged += OnSettingsPathsChanged;
        ModSync.ToastRequested += ShowToast;
        ModSync.StatusChanged += OnModSyncStatusChanged;
        ModSync.ConfirmationRequested += OnModSyncConfirmationRequested;
        UpdateModal.ToastRequested += ShowToast;

        _toastTimer.Interval = TimeSpan.FromSeconds(3.5);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        _updateCheckTimer.Interval = TimeSpan.FromSeconds(60);
        _updateCheckTimer.Tick += async (_, _) => await PeriodicCheckForUpdatesAsync();

        _verificationTimer.Interval = TimeSpan.FromMinutes(2);
        _verificationTimer.Tick += async (_, _) => await PingVerificationAsync();
    }

    public async Task InitializeAsync()
    {
        // Неблокирующая фоновая инициализация Discord RPC
        _ = Task.Run(() => _discordRpcService.Initialize());

        // Параллельная загрузка списка модов и проверка обновлений для максимальной скорости старта
        await Task.WhenAll(
            ModSync.LoadCatalogAsync(Settings.CurrentModsPath),
            CheckForUpdatesAsync()
        );

        _updateCheckTimer.Start();
        _verificationTimer.Start();
        _ = PingVerificationAsync();
    }

    public void ShowToast(string message, bool isError = false)
    {
        RunOnUi(() =>
        {
            ToastText = message;
            IsToastError = isError;
            IsToastVisible = true;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    [RelayCommand]
    public async Task LaunchGameAsync()
    {
        if (IsBusy || ModSync.IsDownloading || ModSync.IsLoadingCatalog) return;

        if (_processSupervisor.IsGameRunning())
        {
            MessageDialogRequested?.Invoke(
                "Игра SCUM уже запущена на вашем компьютере!\n\nПереключитесь на уже открытое окно игры.",
                "Игра уже запущена");
            return;
        }

        // Проверяем актуальность каталога модов
        if (ModSync.AvailableMods.Count == 0)
        {
            await ModSync.LoadCatalogAsync(Settings.CurrentModsPath);
        }

        // Сверка синхронизации перед входом на сервер
        if (ModSync.AvailableMods.Count > 0)
        {
            var (isSynced, missing, obsolete) = await ModSync.CheckSyncStatusAsync(Settings.CurrentModsPath);

            if (!isSynced)
            {
                var reasons = new List<string>();
                if (missing.Count > 0) reasons.Add($"Необходимо скачать или обновить: {missing.Count} модов");
                if (obsolete.Count > 0) reasons.Add($"Обнаружено устаревших или сторонних файлов: {obsolete.Count}");

                string reasonText = string.Join("\n• ", reasons);
                bool syncNow = ConfirmationRequested?.Invoke(
                    $"Внимание: обнаружен рассинхрон модификаций с Яндекс Диском!\n\n• {reasonText}\n\nПеред подключением к серверу необходимо иметь 100% синхронизированный набор модов.\n\nВыполнить синхронизацию прямо сейчас?",
                    "Требуется синхронизация") ?? false;

                if (syncNow)
                {
                    await ModSync.SynchronizeAsync(Settings.CurrentModsPath);
                }
                return;
            }
        }

        string? exe = Settings.CurrentGamePath.FindExecutable();
        if (exe == null)
        {
            ShowToast("SCUM не найден! Укажите путь к игре.", true);
            return;
        }

        try
        {
            StatusText = $"Запуск SCUM ({LaunchMode.ToKey().ToUpperInvariant()})...";
            IsStatusOk = true;

            // Фоновый пинг верификации
            _ = Task.Run(async () =>
            {
                try { await PingVerificationAsync(); }
                catch (Exception pingEx) { _logger?.LogDebug(pingEx, "Ошибка фонового пинга верификации при запуске SCUM."); }
            });

            var result = _gameLauncherService.LaunchGame(Settings.CurrentGamePath, LaunchMode);
            if (result.IsSuccess)
            {
                ShowToast("SCUM запущен!");
                GameLaunchedSuccessfully?.Invoke();

                // Оптимизация памяти лаунчера: сброс неиспользуемого рабочего набора для освобождения ОЗУ игре SCUM
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    MemoryOptimizer.TrimWorkingSet();
                });
            }
            else
            {
                ShowToast(result.Error ?? "Ошибка запуска", true);
                StatusText = "Ошибка запуска";
                IsStatusOk = false;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка запуска: {ex.Message}", true);
            StatusText = "Ошибка запуска";
            IsStatusOk = false;
        }
    }

    [RelayCommand]
    public async Task SynchronizeModsAsync()
    {
        await ModSync.SynchronizeAsync(Settings.CurrentModsPath);
    }

    [RelayCommand]
    public async Task DeleteModsAsync()
    {
        await ModSync.DeleteAllModsAsync(Settings.CurrentModsPath);
    }

    [RelayCommand]
    public void SetLaunchMode(string modeKey)
    {
        LaunchMode = LaunchModeExtensions.FromKey(modeKey);
        var cfg = _configRepository.LoadConfig();
        cfg.LaunchMode = modeKey;
        _configRepository.SaveConfig(cfg);
    }

    [RelayCommand]
    public async Task WipeClientAsync()
    {
        bool confirmed = ConfirmationRequested?.Invoke(
            "Внимание: будет полностью удален кэш клиента SCUM (%LOCALAPPDATA%\\SCUM)!\n\nЭто действие сбросит локальные временные файлы и графический кэш игры (помогает при сбоях и вылетах SCUM).\n\nПродолжить?",
            "Очистка клиента SCUM") ?? false;

        if (!confirmed) return;

        IsBusy = true;
        StatusText = "Очистка клиента SCUM...";
        try
        {
            var res = await _maintenanceService.WipeClientCacheAsync();
            if (res.IsSuccess)
            {
                string msg = res.Value.wasEmpty
                    ? "Папка клиента SCUM уже была чиста!\n\nВременные файлы и кэш отсутствуют."
                    : "Очистка клиента SCUM успешно завершена!\n\nПапка %LOCALAPPDATA%\\SCUM полностью удалена.\nПри следующем входе игра создаст новые чистые файлы конфигурации.";

                MessageDialogRequested?.Invoke(msg, "Очистка завершена");
                ShowToast("Клиент SCUM успешно очищен!");
                StatusText = "Клиент SCUM успешно очищен";
                IsStatusOk = true;
            }
            else
            {
                ShowToast("Часть файлов заблокирована системой", true);
                StatusText = "Очистка завершена с предупреждением";
                IsStatusOk = false;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка: {ex.Message}", true);
            StatusText = "Ошибка очистки";
            IsStatusOk = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CopyServerInfo()
    {
        try
        {
            Clipboard.SetText(ServerIp);
            ShowToast("IP сервера скопирован в буфер обмена!");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Не удалось скопировать адрес сервера в буфер обмена.");
        }
    }

    [RelayCommand]
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger?.LogWarning("Заблокирована попытка открытия недопустимого протокола/URL: '{Url}'", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Не удалось открыть URL '{Url}'.", url);
            ShowToast($"Не удалось открыть ссылку: {ex.Message}", true);
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update != null)
            {
                UpdateModal.SetAvailableUpdate(update);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Фоновая проверка обновлений завершилась исключением.");
        }
    }

    private async Task PeriodicCheckForUpdatesAsync()
    {
        if (UpdateModal.IsDownloadingUpdate || ModSync.IsDownloading) return;
        await CheckForUpdatesAsync();
        MemoryOptimizer.TrimWorkingSet();
    }

    private async Task PingVerificationAsync()
    {
        try
        {
            string? steamId = _verificationService.GetActiveSteamId64(Settings.CurrentGamePath);
            if (!string.IsNullOrEmpty(steamId))
            {
                await _verificationService.SendVerificationToAllAsync(steamId, AppVersionText, ModSync.InstalledCount, ServerIp);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Не удалось отправить пакет верификации.");
        }
    }

    private async void OnSettingsPathsChanged()
    {
        await ModSync.LoadCatalogAsync(Settings.CurrentModsPath);
    }

    private void OnModSyncStatusChanged(string msg, bool ok)
    {
        StatusText = msg;
        IsStatusOk = ok;
    }

    private bool OnModSyncConfirmationRequested(string msg, string title)
    {
        return ConfirmationRequested?.Invoke(msg, title) ?? true;
    }

    public void Cleanup()
    {
        Settings.PathsChanged -= OnSettingsPathsChanged;
        ModSync.ToastRequested -= ShowToast;
        ModSync.StatusChanged -= OnModSyncStatusChanged;
        ModSync.ConfirmationRequested -= OnModSyncConfirmationRequested;
        UpdateModal.ToastRequested -= ShowToast;

        _updateCheckTimer.Stop();
        _verificationTimer.Stop();
        _toastTimer.Stop();
        _discordRpcService.Dispose();
    }
}
