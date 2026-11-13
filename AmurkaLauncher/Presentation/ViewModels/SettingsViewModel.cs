using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Models;
using Microsoft.Win32;
using System.IO;

namespace AmurkaLauncher.Presentation.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IScumDetectionService _scumDetectionService;
    private readonly IConfigRepository _configRepository;
    private LauncherConfig _config;

    [ObservableProperty]
    private string _gamePathText = string.Empty;

    partial void OnGamePathTextChanged(string value) => UpdateGameStatus();

    [ObservableProperty]
    private string _modsPathText = string.Empty;

    [ObservableProperty]
    private bool _isGameFound;

    [ObservableProperty]
    private string _gameStatusDescription = "SCUM не найден";

    public event Action? PathsChanged;

    public SettingsViewModel(IScumDetectionService scumDetectionService, IConfigRepository configRepository)
    {
        _scumDetectionService = scumDetectionService;
        _configRepository = configRepository;
        _config = _configRepository.LoadConfig();
        InitializeFromConfig();
    }

    public GamePath CurrentGamePath => GamePath.From(GamePathText);
    public ModsPath CurrentModsPath => ModsPath.From(ModsPathText);

    public void InitializeFromConfig()
    {
        _config = _configRepository.LoadConfig();
        GamePath gp = GamePath.From(_config.GamePath);
        ModsPath mp = ModsPath.From(_config.ModsPath);

        bool changed = _scumDetectionService.ResolveAndEnsurePaths(ref gp, ref mp);
        GamePathText = gp.Value;
        ModsPathText = mp.Value;

        UpdateGameStatus();

        if (changed)
        {
            SavePathsToConfig();
        }
    }

    public void UpdateGameStatus()
    {
        string? exe = _scumDetectionService.FindScumExe(CurrentGamePath);
        IsGameFound = exe != null;
        GameStatusDescription = IsGameFound
            ? "SCUM найден"
            : (string.IsNullOrWhiteSpace(GamePathText) ? "Укажите путь к SCUM" : "SCUM не найден");
    }

    [RelayCommand]
    public void BrowseGameFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку SCUM",
            InitialDirectory = Directory.Exists(GamePathText) ? GamePathText : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            GamePath normalized = _scumDetectionService.NormalizeGameRoot(dialog.FolderName);
            ModsPath resolvedMods = _scumDetectionService.GetModsPath(normalized);
            resolvedMods.EnsureCreated();

            GamePathText = normalized.Value;
            ModsPathText = resolvedMods.Value;
            UpdateGameStatus();
            SavePathsToConfig();
            PathsChanged?.Invoke();
        }
    }

    [RelayCommand]
    public void BrowseModsFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку ~mods",
            InitialDirectory = Directory.Exists(ModsPathText) ? ModsPathText : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            ModsPath chosen = ModsPath.From(dialog.FolderName);
            chosen.EnsureCreated();

            ModsPathText = chosen.Value;
            SavePathsToConfig();
            PathsChanged?.Invoke();
        }
    }

    [RelayCommand]
    public async Task AutoDetectGameAsync()
    {
        var detectResult = await _scumDetectionService.AutoDetectAsync();
        if (detectResult.IsSuccess && !detectResult.Value.IsEmpty)
        {
            GamePath normalized = detectResult.Value;
            ModsPath resolvedMods = _scumDetectionService.GetModsPath(normalized);
            resolvedMods.EnsureCreated();

            GamePathText = normalized.Value;
            ModsPathText = resolvedMods.Value;
            UpdateGameStatus();
            SavePathsToConfig();
            PathsChanged?.Invoke();
        }
    }

    [RelayCommand]
    public void GamePathLostFocus() => OnGamePathInputLostFocus();

    [RelayCommand]
    public void ModsPathLostFocus() => OnModsPathInputLostFocus();

    public void OnGamePathInputLostFocus()
    {
        if (string.IsNullOrWhiteSpace(GamePathText)) return;

        GamePath normalized = _scumDetectionService.NormalizeGameRoot(GamePathText);
        if (normalized.Value != GamePathText)
        {
            ModsPath resolvedMods = _scumDetectionService.GetModsPath(normalized);
            resolvedMods.EnsureCreated();

            GamePathText = normalized.Value;
            ModsPathText = resolvedMods.Value;
            UpdateGameStatus();
            SavePathsToConfig();
            PathsChanged?.Invoke();
        }
    }

    public void OnModsPathInputLostFocus()
    {
        if (string.IsNullOrWhiteSpace(ModsPathText)) return;

        ModsPath normalized = ModsPath.From(ModsPathText);
        normalized.EnsureCreated();

        if (normalized.Value != ModsPathText)
        {
            ModsPathText = normalized.Value;
            SavePathsToConfig();
            PathsChanged?.Invoke();
        }
    }

    private void SavePathsToConfig()
    {
        _config.GamePath = GamePathText;
        _config.ModsPath = ModsPathText;
        _configRepository.SaveConfig(_config);
    }
}
