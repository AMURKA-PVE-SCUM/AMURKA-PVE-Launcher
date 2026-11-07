using System.Windows;

namespace AmurkaLauncher.Controls;

public enum AmurkaDialogButtons
{
    Ok,
    YesNo,
    OkCancel
}

public enum AmurkaDialogIcon
{
    Info,
    Warning,
    Question,
    Error,
    Success
}

public static class AmurkaMessageBox
{
    public static bool? Show(
        string message,
        string title = "AMURKA PVE",
        AmurkaDialogButtons buttons = AmurkaDialogButtons.Ok,
        AmurkaDialogIcon icon = AmurkaDialogIcon.Info,
        Window? owner = null,
        string? confirmText = null,
        string? cancelText = null,
        bool isDanger = false)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() =>
                Show(message, title, buttons, icon, owner, confirmText, cancelText, isDanger));
        }

        owner ??= Application.Current?.MainWindow;
        var dialog = new AmurkaDialog(message, title, buttons, icon, confirmText, cancelText, isDanger);

        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }
}
