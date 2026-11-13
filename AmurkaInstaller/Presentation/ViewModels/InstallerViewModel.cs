using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Core.Domain.Results;
using Amurka.Shared.IO;
using Microsoft.Extensions.Logging;
using AmurkaInstaller.Core.Domain.Enums;
using AmurkaInstaller.Core.Domain.Models;
using AmurkaInstaller.Infrastructure.Abstractions;

namespace AmurkaInstaller.Presentation.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class InstallerViewModel : ObservableObject
{
    private static readonly string AppVersion = AppConstants.Version;

    private readonly IPayloadExtractor _payloadExtractor;
    private readonly IShortcutManager _shortcutManager;
    private readonly IInstallerRegistryService _registryService;
    private readonly IInstallerProcessManager _processManager;
    private readonly IInstallLocationDetector _locationDetector;
    private readonly InstallerOptions _options;
    private readonly ILogger<InstallerViewModel>? _logger;

    public InstallerMode Mode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep))]
    [NotifyPropertyChangedFor(nameof(IsInstallingStep))]
    [NotifyPropertyChangedFor(nameof(IsFinishedStep))]
    [NotifyPropertyChangedFor(nameof(IsUninstallStep))]
    private InstallerStep _currentStep = InstallerStep.Welcome;

    public bool IsWelcomeStep => CurrentStep == InstallerStep.Welcome;
    public bool IsInstallingStep => CurrentStep == InstallerStep.Installing;
    public bool IsFinishedStep => CurrentStep == InstallerStep.Finished;
    public bool IsUninstallStep => CurrentStep == InstallerStep.UninstallConfirm;

    [ObservableProperty]
    private string _appVersionText = $"Версия: v{AppVersion}";

    [ObservableProperty]
    private string _installPath = string.Empty;

    partial void OnInstallPathChanged(string value)
    {
        DiskSpaceInfo = _locationDetector.GetDiskSpaceInfo(value);
    }

    [ObservableProperty]
    private string _diskSpaceInfo = string.Empty;

    [ObservableProperty]
    private bool _createDesktopShortcut = true;

    [ObservableProperty]
    private bool _createStartMenuShortcut = true;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _progressStatus = "Подготовка файлов...";

    [ObservableProperty]
    private string _installTitle = "Установка AMURKA PVE Launcher...";

    [ObservableProperty]
    private string _finishedTitle = "Установка успешно завершена!";

    [ObservableProperty]
    private string _finishedDescription = "AMURKA PVE Launcher готов к работе и запуску.";

    [ObservableProperty]
    private string _installedPath = string.Empty;

    [ObservableProperty]
    private bool _isFinishedInstall = true;

    public event Action? RequestClose;
    public event Action<string, string, bool>? ShowMessageRequested;

    public InstallerViewModel(
        IPayloadExtractor payloadExtractor,
        IShortcutManager shortcutManager,
        IInstallerRegistryService registryService,
        IInstallerProcessManager processManager,
        IInstallLocationDetector locationDetector,
        InstallerOptions options,
        ILogger<InstallerViewModel>? logger = null)
    {
        _payloadExtractor = payloadExtractor;
        _shortcutManager = shortcutManager;
        _registryService = registryService;
        _processManager = processManager;
        _locationDetector = locationDetector;
        _options = options;
        _logger = logger;
        Mode = options.Mode;
        CreateDesktopShortcut = options.CreateDesktopShortcut;
        CreateStartMenuShortcut = options.CreateStartMenuShortcut;

        if (Mode == InstallerMode.Uninstall)
        {
            CurrentStep = InstallerStep.UninstallConfirm;
        }
        else
        {
            InstallPath = !string.IsNullOrWhiteSpace(options.TargetPath)
                ? options.TargetPath
                : _locationDetector.DetermineDefaultInstallPath(Mode == InstallerMode.Update);
            DiskSpaceInfo = _locationDetector.GetDiskSpaceInfo(InstallPath);
            CurrentStep = InstallerStep.Welcome;
        }
    }

    public async Task InitializeAsync()
    {
        if (Mode == InstallerMode.Update || _options.IsSilent)
        {
            await Task.Delay(200);
            if (Mode == InstallerMode.Update)
            {
                await _processManager.WaitForLauncherExitAsync(TimeSpan.FromSeconds(3));
            }
            await RunInstallationAsync();

            if (_options.IsSilent)
            {
                RequestClose?.Invoke();
            }
        }
    }

    [RelayCommand]
    public void BrowseInstallFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для установки AMURKA PVE Launcher",
            InitialDirectory = Directory.Exists(InstallPath) ? InstallPath : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName;
            if (!chosen.EndsWith("AMURKA PVE Launcher", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.Combine(chosen, "AMURKA PVE Launcher");
            }
            InstallPath = chosen;
        }
    }

    [RelayCommand]
    public async Task StartInstallationAsync()
    {
        await RunInstallationAsync();
    }

    private async Task RunInstallationAsync()
    {
        string targetDir = InstallPath.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            ShowMessageRequested?.Invoke("Пожалуйста, укажите корректную папку установки.", "AMURKA PVE Launcher", false);
            return;
        }

        CurrentStep = InstallerStep.Installing;
        InstallTitle = Mode == InstallerMode.Update ? "Обновление AMURKA PVE Launcher..." : "Установка AMURKA PVE Launcher...";
        ProgressPercent = 0;
        ProgressStatus = "Завершение активных процессов...";

        Result preflight = _payloadExtractor.RunPreFlightChecks(targetDir);
        if (preflight.IsFailure)
        {
            ShowMessageRequested?.Invoke(preflight.Error!, "Проверка каталога установки", true);
            CurrentStep = InstallerStep.Welcome;
            return;
        }

        // Гарантированно закрываем запущенный экземпляр лаунчера перед перезаписью
        await Task.Run(() => _processManager.KillRunningLauncherProcesses(targetDir));
        await _processManager.WaitForLauncherExitAsync(TimeSpan.FromSeconds(2), targetDir);

        ProgressStatus = "Подготовка к распаковке...";

        var progress = new Progress<(int percent, string status)>(report =>
        {
            ProgressPercent = report.percent;
            ProgressStatus = report.status;
        });

        Result extractResult = await _payloadExtractor.ExtractPayloadAsync(targetDir, progress);
        if (extractResult.IsFailure)
        {
            string err = !string.IsNullOrWhiteSpace(extractResult.Error)
                ? extractResult.Error
                : "Не удалось распаковать файлы лаунчера. Проверьте права доступа к выбранной папке.";
            ShowMessageRequested?.Invoke(err, "Ошибка установки", true);
            CurrentStep = InstallerStep.Welcome;
            return;
        }

        // Регистрация в реестре
        _registryService.RegisterInstallation(targetDir, AppVersion);

        // Создание ярлыков
        if (CreateDesktopShortcut)
        {
            _shortcutManager.CreateDesktopShortcut(targetDir);
        }
        if (CreateStartMenuShortcut)
        {
            _shortcutManager.CreateStartMenuShortcuts(targetDir);
        }

        if (Mode == InstallerMode.Update)
        {
            _processManager.LaunchInstalledApp(targetDir);
            RequestClose?.Invoke();
            return;
        }

        InstalledPath = targetDir;
        FinishedTitle = "Установка успешно завершена!";
        FinishedDescription = "AMURKA PVE Launcher готов к работе и запуску.";
        IsFinishedInstall = true;
        CurrentStep = InstallerStep.Finished;
    }

    [RelayCommand]
    public async Task ConfirmUninstallAsync()
    {
        try
        {
            CurrentStep = InstallerStep.Installing;
            InstallTitle = "Удаление AMURKA PVE Launcher...";
            ProgressPercent = 15;
            ProgressStatus = "Завершение активных процессов...";

            await Task.Run(() => _processManager.KillRunningLauncherProcesses());

            ProgressPercent = 40;
            ProgressStatus = "Удаление ярлыков и меню «Пуск»...";
            _shortcutManager.RemoveShortcuts();

            ProgressPercent = 65;
            ProgressStatus = "Очистка записей в реестре Windows...";
            _registryService.UnregisterInstallation();

            ProgressPercent = 85;
            ProgressStatus = "Очистка каталога программы...";
            string? installDir = _registryService.GetExistingInstallLocation();
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                if (File.Exists(Path.Combine(baseDir, "AMURKA PVE Launcher.exe")))
                {
                    installDir = baseDir;
                }
            }

            if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
            {
                _processManager.ScheduleSelfDirectoryDeletion(installDir);
            }

            ProgressPercent = 100;
            ProgressStatus = "Удаление успешно завершено.";
            await Task.Delay(400);

            FinishedTitle = "Удаление завершено";
            FinishedDescription = "AMURKA PVE Launcher успешно удален с вашего компьютера.";
            IsFinishedInstall = false;
            CurrentStep = InstallerStep.Finished;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка удаления приложения из '{InstallPath}'.", InstallPath);
            ShowMessageRequested?.Invoke($"Ошибка при удалении: {ex.Message}", "Ошибка", true);
            RequestClose?.Invoke();
        }
    }

    [RelayCommand]
    public void LaunchApp()
    {
        _processManager.LaunchInstalledApp(InstalledPath);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void Close()
    {
        RequestClose?.Invoke();
    }
}
