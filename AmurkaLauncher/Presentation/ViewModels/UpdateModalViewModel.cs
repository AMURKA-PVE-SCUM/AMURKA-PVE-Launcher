using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using System.IO;

namespace AmurkaLauncher.Presentation.ViewModels;

public sealed partial class UpdateModalViewModel(IUpdateService updateService) : ViewModelBase
{
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    [ObservableProperty]
    private string _updateDescription = "Нажмите «Скачать», чтобы получить новую версию";

    [ObservableProperty]
    private double _updateProgressPercent;

    [ObservableProperty]
    private string _updateProgressLabel = "0%";

    private UpdateInfo? _currentUpdate;
    private string? _downloadedInstallerPath;

    public event Action<string, bool>? ToastRequested;

    public void SetAvailableUpdate(UpdateInfo update)
    {
        _currentUpdate = update;
        UpdateVersion = update.Version;
        IsVisible = true;
    }

    [RelayCommand]
    public async Task DownloadUpdateAsync()
    {
        if (_currentUpdate == null || IsDownloadingUpdate) return;

        if (!string.IsNullOrEmpty(_downloadedInstallerPath) && File.Exists(_downloadedInstallerPath))
        {
            updateService.InstallUpdate(_downloadedInstallerPath);
            return;
        }

        IsDownloadingUpdate = true;
        try
        {
            _downloadedInstallerPath = await updateService.DownloadUpdateAsync(
                _currentUpdate,
                (percent, downloaded, total) =>
                {
                    RunOnUi(() =>
                    {
                        UpdateProgressPercent = percent;
                        UpdateProgressLabel = $"{percent}% ({FormatBytes(downloaded)} / {FormatBytes(total)})";
                    });
                });

            UpdateDescription = "Загрузка завершена! Запуск установщика...";
            await Task.Delay(400);
            if (!string.IsNullOrEmpty(_downloadedInstallerPath))
            {
                updateService.InstallUpdate(_downloadedInstallerPath);
                System.Windows.Application.Current?.Shutdown();
            }
        }
        catch (Exception ex)
        {
            ToastRequested?.Invoke($"Ошибка скачивания обновления: {ex.Message}", true);
            UpdateDescription = "Ошибка скачивания. Повторите попытку.";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    public void Dismiss()
    {
        IsVisible = false;
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
