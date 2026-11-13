using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AmurkaLauncher.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Presentation.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class ModSyncViewModel : ViewModelBase
{
    private readonly IModSyncEngine _modSyncEngine;
    private readonly IModCatalogRepository _catalogRepository;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly ILogger<ModSyncViewModel>? _logger;

    private List<ModItem> _availableMods = [];
    private List<ManagedMod> _obsoleteMods = [];

    [ObservableProperty]
    private int _availableCount;

    [ObservableProperty]
    private int _installedCount;

    [ObservableProperty]
    private bool _isSyncBannerVisible;

    [ObservableProperty]
    private string _syncBannerTitle = string.Empty;

    [ObservableProperty]
    private string _syncBannerDescription = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isLoadingCatalog;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private string _downloadProgressLabel = string.Empty;

    public event Action<string, bool>? ToastRequested;
    public event Action<string, bool>? StatusChanged;
    public event Func<string, string, bool>? ConfirmationRequested;

    public ModSyncViewModel(
        IModSyncEngine modSyncEngine,
        IModCatalogRepository catalogRepository,
        IProcessSupervisor processSupervisor,
        ILogger<ModSyncViewModel>? logger = null)
    {
        _modSyncEngine = modSyncEngine;
        _catalogRepository = catalogRepository;
        _processSupervisor = processSupervisor;
        _logger = logger;
    }

    public List<ModItem> AvailableMods => _availableMods;

    public async Task<bool> LoadCatalogAsync(ModsPath modsPath, CancellationToken ct = default)
    {
        if (IsDownloading || IsLoadingCatalog) return false;

        IsLoadingCatalog = true;
        StatusChanged?.Invoke("Проверка версий модов...", true);

        try
        {
            _availableMods = await _catalogRepository.FetchAvailableModsAsync(null, ct);

            if (!modsPath.IsEmpty)
            {
                await _modSyncEngine.RefreshInstalledStateAsync(_availableMods, modsPath, ct);
                _obsoleteMods = await _modSyncEngine.FindObsoleteModsAsync(_availableMods, modsPath, ct);
            }
            else
            {
                foreach (var mod in _availableMods) mod.IsInstalled = false;
                _obsoleteMods = [];
            }

            UpdateCounts();
            RefreshBanner(modsPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка загрузки каталога модов.");
            StatusChanged?.Invoke($"Ошибка: {ex.Message}", false);
            ToastRequested?.Invoke("Не удалось загрузить список модов", true);
            return false;
        }
        finally
        {
            IsLoadingCatalog = false;
            _ = Task.Delay(500, CancellationToken.None).ContinueWith(_ => Amurka.Shared.Platform.MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);
        }
    }

    public void UpdateCounts()
    {
        AvailableCount = _availableMods.Count;
        InstalledCount = _availableMods.Count(m => m.IsInstalled);
    }

    public void RefreshBanner(ModsPath modsPath)
    {
        if (_availableMods.Count == 0) return;

        if (modsPath.IsEmpty)
        {
            SyncBannerTitle = "Укажите путь к папке SCUM";
            SyncBannerDescription = $"Доступно {_availableMods.Count} актуальных модов. Укажите папку с игрой в настройках.";
            StatusChanged?.Invoke("Укажите путь к игре в настройках", false);
            IsSyncBannerVisible = true;
            return;
        }

        int missingCount = _availableMods.Count(m => !m.IsInstalled);
        var untracked = _modSyncEngine.FindUntrackedOrObsoleteFiles(_availableMods, modsPath);
        var extraFileNames = new HashSet<string>(_obsoleteMods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var file in untracked) extraFileNames.Add(file);
        int extraCount = extraFileNames.Count;

        if (missingCount == 0 && extraCount == 0)
        {
            IsSyncBannerVisible = false;
            StatusChanged?.Invoke("Моды синхронизированы с сервером", true);
            return;
        }

        if (missingCount > 0 && extraCount > 0)
        {
            SyncBannerTitle = "Требуется синхронизация модификаций";
            SyncBannerDescription = $"Скачать с сервера: {missingCount} · Удалить сторонних: {extraCount}";
            StatusChanged?.Invoke($"Требуется синхронизация ({missingCount} из {_availableMods.Count})", false);
        }
        else if (missingCount > 0)
        {
            SyncBannerTitle = "Доступны обновления модификаций";
            SyncBannerDescription = $"Необходимо скачать: {missingCount} из {_availableMods.Count} модов";
            StatusChanged?.Invoke($"Требуется скачать: {missingCount} из {_availableMods.Count}", false);
        }
        else
        {
            SyncBannerTitle = "Обнаружены сторонние файлы";
            SyncBannerDescription = $"В папке найдено {extraCount} сторонних файлов";
            StatusChanged?.Invoke($"Лишних файлов в папке: {extraCount}", false);
        }

        IsSyncBannerVisible = true;
    }

    public async Task<(bool isSynced, List<ModItem> missing, List<string> obsolete)> CheckSyncStatusAsync(ModsPath modsPath, CancellationToken ct = default)
    {
        if (modsPath.IsEmpty || _availableMods.Count == 0)
        {
            return (false, _availableMods, []);
        }

        return await _modSyncEngine.CheckSynchronizationAsync(_availableMods, modsPath, ct);
    }

    public async Task<bool> SynchronizeAsync(ModsPath modsPath, CancellationToken ct = default)
    {
        if (IsDownloading || IsLoadingCatalog) return false;

        if (modsPath.IsEmpty)
        {
            ToastRequested?.Invoke("Укажите папку модов", true);
            return false;
        }

        if (_processSupervisor.IsGameRunning())
        {
            ToastRequested?.Invoke("Закройте SCUM перед синхронизацией модов", true);
            return false;
        }

        await _modSyncEngine.RefreshInstalledStateAsync(_availableMods, modsPath, ct);
        var missingOrCorrupt = _availableMods.Where(m => !m.IsInstalled).ToList();
        var untracked = _modSyncEngine.FindUntrackedOrObsoleteFiles(_availableMods, modsPath);

        if (missingOrCorrupt.Count == 0 && untracked.Count == 0)
        {
            ToastRequested?.Invoke("Моды полностью синхронизированы", false);
            StatusChanged?.Invoke("Моды полностью синхронизированы", true);
            RefreshBanner(modsPath);
            return true;
        }

        IsDownloading = true;
        int success = 0;
        int errors = 0;
        int removed = 0;

        try
        {
            var progress = new Progress<SyncProgressReport>(report =>
            {
                RunOnUi(() =>
                {
                    DownloadProgressPercent = report.Percent;
                    DownloadProgressLabel = $"{report.CurrentFile} — {FormatBytes(report.DownloadedBytes)} / {FormatBytes(report.TotalBytes)}";
                    StatusChanged?.Invoke($"Загрузка: {report.CurrentFile} ({report.Percent}%)", true);
                });
            });

            if (missingOrCorrupt.Count > 0)
            {
                var downloadResult = await _modSyncEngine.DownloadAllModsAsync(missingOrCorrupt, modsPath, progress, ct);
                if (downloadResult.IsSuccess)
                {
                    success = downloadResult.Value.success;
                    errors = downloadResult.Value.errors;
                }
                else
                {
                    errors = missingOrCorrupt.Count;
                }
            }

            if (errors == 0)
            {
                removed = await _modSyncEngine.RemoveObsoleteAndUntrackedModsAsync(_availableMods, modsPath, ct);
            }

            await _modSyncEngine.RefreshInstalledStateAsync(_availableMods, modsPath, ct);
            _obsoleteMods = await _modSyncEngine.FindObsoleteModsAsync(_availableMods, modsPath, ct);

            UpdateCounts();
            RefreshBanner(modsPath);

            if (errors > 0)
            {
                ToastRequested?.Invoke($"Не удалось обновить моды. Ошибок: {errors}. Повторите загрузку.", true);
                StatusChanged?.Invoke("Обновление модов не завершено", false);
                return false;
            }
            else if (success > 0 && removed > 0)
            {
                ToastRequested?.Invoke($"Синхронизация завершена: скачано {success}, удалено лишних {removed}", false);
                StatusChanged?.Invoke($"Синхронизировано (+{success}, -{removed})", true);
            }
            else if (success > 0)
            {
                ToastRequested?.Invoke($"Синхронизация завершена: скачано {success}", false);
                StatusChanged?.Invoke($"Скачано модов: {success}", true);
            }
            else if (removed > 0)
            {
                ToastRequested?.Invoke($"Папка очищена: удалено лишних файлов: {removed}", false);
                StatusChanged?.Invoke($"Удалено лишних файлов: {removed}", true);
            }
            else
            {
                ToastRequested?.Invoke("Моды полностью синхронизированы", false);
                StatusChanged?.Invoke("Моды полностью синхронизированы", true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка синхронизации модов.");
            ToastRequested?.Invoke($"Ошибка: {ex.Message}", true);
            StatusChanged?.Invoke("Ошибка скачивания", false);
            return false;
        }
        finally
        {
            IsDownloading = false;
            _ = Task.Delay(500, CancellationToken.None).ContinueWith(_ => Amurka.Shared.Platform.MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);
        }
    }

    public async Task DeleteAllModsAsync(ModsPath modsPath)
    {
        if (IsDownloading || IsLoadingCatalog) return;

        if (_processSupervisor.IsGameRunning())
        {
            ToastRequested?.Invoke("Закройте SCUM перед удалением модов", true);
            return;
        }

        bool confirmed = ConfirmationRequested?.Invoke(
            "Вы действительно хотите удалить все файлы из папки ~mods?\n\nЭто действие полностью очистит каталог модификаций.",
            "Подтверждение удаления") ?? true;

        if (!confirmed) return;

        try
        {
            int removed = _modSyncEngine.DeleteAllMods(modsPath);
            foreach (var mod in _availableMods) mod.IsInstalled = false;
            await _modSyncEngine.RefreshInstalledStateAsync(_availableMods, modsPath);
            _obsoleteMods = [];

            UpdateCounts();
            RefreshBanner(modsPath);

            ToastRequested?.Invoke($"Удалено файлов: {removed}", false);
            StatusChanged?.Invoke($"Удалено файлов: {removed}", true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка удаления всех модов в '{ModsPath}'.", modsPath.Value);
            ToastRequested?.Invoke($"Ошибка удаления: {ex.Message}", true);
        }
    }

    [RelayCommand]
    public void DismissBanner()
    {
        IsSyncBannerVisible = false;
    }

    private static string FormatBytes(long bytes)
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
        return $"{size:0.##} {units[i]}";
    }
}
