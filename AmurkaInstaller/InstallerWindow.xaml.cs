using System.Windows;
using System.Windows.Input;
using Amurka.Shared.UI.Controls;
using AmurkaInstaller.Presentation.ViewModels;

namespace AmurkaInstaller;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class InstallerWindow : Window
{
    private readonly InstallerViewModel _viewModel;

    public InstallerWindow(InstallerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        LoadWindowIcon();

        _viewModel.RequestClose += Close;
        _viewModel.ShowMessageRequested += (msg, title, isError) =>
        {
            AmurkaMessageBox.Show(
                msg,
                title,
                AmurkaDialogButtons.Ok,
                isError ? AmurkaDialogIcon.Error : AmurkaDialogIcon.Warning,
                this);
        };

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                AmurkaMessageBox.Show(
                    $"Ошибка при инициализации установщика:\n\n{ex.Message}",
                    "Сбой запуска",
                    AmurkaDialogButtons.Ok,
                    AmurkaDialogIcon.Error,
                    this);
            }
        };

        Closed += (_, _) =>
        {
            _viewModel.RequestClose -= Close;
        };
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadWindowIcon()
    {
        string[] candidates =
        [
            "pack://application:,,,/AmurkaInstaller;component/Resources/icon.ico",
            "pack://application:,,,/AMURKA PVE Launcher;component/Resources/icon.ico",
            "pack://application:,,,/Amurka.Shared;component/Resources/icon.ico"
        ];

        foreach (string uriStr in candidates)
        {
            try
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri(uriStr, UriKind.RelativeOrAbsolute));
                Icon = frame;
                if (TitleIcon != null)
                {
                    TitleIcon.Source = frame;
                }
                break;
            }
            catch
            {
                // Поиск по следующему pack URI кандидату
            }
        }
    }
}
