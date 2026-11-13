using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Amurka.Shared.Platform;
using Amurka.Shared.UI.Controls;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Presentation.ViewModels;

namespace AmurkaLauncher;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly ITrayManagerService _trayManager;
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel, ITrayManagerService trayManager)
    {
        ViewModel = viewModel;
        _trayManager = trayManager;

        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.ConfirmationRequested += OnConfirmationRequested;
        ViewModel.MessageDialogRequested += OnMessageDialogRequested;
        ViewModel.GameLaunchedSuccessfully += OnGameLaunchedSuccessfully;

        Loaded += MainWindow_Loaded;
        Deactivated += OnWindowDeactivated;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        _ = Task.Delay(500).ContinueWith(_ => MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _trayManager.Initialize(
            this,
            restoreCallback: () => ViewModel.CheckForUpdatesCommand.Execute(null),
            exitCallback: CloseFromTray);

        // Гарантированный сброс первичных аллокаций XAML/JIT через 2 секунды после отображения окна
        _ = Task.Delay(2000).ContinueWith(_ => MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);

        await ViewModel.InitializeAsync();

        // Сброс временных аллокаций после загрузки каталога и проверки сети
        _ = Task.Delay(800).ContinueWith(_ => MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);
    }

    private bool OnConfirmationRequested(string message, string title) =>
        AmurkaMessageBox.Show(message, title, AmurkaDialogButtons.YesNo, AmurkaDialogIcon.Question, this) == true;

    private void OnMessageDialogRequested(string message, string title) =>
        AmurkaMessageBox.Show(message, title, AmurkaDialogButtons.Ok, AmurkaDialogIcon.Info, this);

    private void OnGameLaunchedSuccessfully() =>
        _trayManager.MinimizeToTray(showBalloon: true);

    public void RestoreFromTray() => _trayManager.RestoreFromTray();

    private void GamePathInput_LostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.Settings.OnGamePathInputLostFocus();

    private void ModsPathInput_LostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.Settings.OnModsPathInputLostFocus();

    private void CloseFromTray()
    {
        Close();
        Application.Current?.Shutdown();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (TryFindResource("IconWinRestoreGeometry") is Geometry restoreGeom)
                MaximizeIconPath.Data = restoreGeom;
            MaximizeBtn.ToolTip = "Восстановить";
        }
        else if (WindowState == WindowState.Normal)
        {
            if (TryFindResource("IconWinMaximizeGeometry") is Geometry maxGeom)
                MaximizeIconPath.Data = maxGeom;
            MaximizeBtn.ToolTip = "Развернуть";
        }
        else if (WindowState == WindowState.Minimized)
        {
            _trayManager.MinimizeToTray(showBalloon: true);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.ModSync.IsDownloading || ViewModel.UpdateModal.IsDownloadingUpdate)
        {
            bool? confirm = AmurkaMessageBox.Show(
                "Сейчас выполняется загрузка файлов.\n\nВы уверены, что хотите прервать процесс и закрыть лаунчер?",
                "Подтверждение выхода",
                AmurkaDialogButtons.YesNo,
                AmurkaDialogIcon.Warning,
                this,
                confirmText: "Выйти",
                cancelText: "Отмена",
                isDanger: true);

            if (confirm != true)
            {
                e.Cancel = true;
                return;
            }
        }

        Loaded -= MainWindow_Loaded;
        Deactivated -= OnWindowDeactivated;
        ViewModel.ConfirmationRequested -= OnConfirmationRequested;
        ViewModel.MessageDialogRequested -= OnMessageDialogRequested;
        ViewModel.GameLaunchedSuccessfully -= OnGameLaunchedSuccessfully;

        ViewModel.Cleanup();
        _trayManager.Dispose();

        base.OnClosing(e);
        Application.Current?.Shutdown();
    }
}
