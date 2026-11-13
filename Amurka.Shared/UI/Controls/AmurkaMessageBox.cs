using System.Windows;

namespace Amurka.Shared.UI.Controls;

public enum AmurkaDialogButtons { Ok, YesNo, OkCancel }
public enum AmurkaDialogIcon { Info, Warning, Question, Error, Success }

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
        Application? app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() =>
                ShowOnUiThread(message, title, buttons, icon, owner, confirmText, cancelText, isDanger));
        }

        return ShowOnUiThread(message, title, buttons, icon, owner, confirmText, cancelText, isDanger);
    }

    private static bool? ShowOnUiThread(
        string message,
        string title,
        AmurkaDialogButtons buttons,
        AmurkaDialogIcon icon,
        Window? owner,
        string? confirmText,
        string? cancelText,
        bool isDanger)
    {
        var dialog = new AmurkaDialog(message, title, buttons, icon, confirmText, cancelText, isDanger);

        Window? resolvedOwner = owner;
        if (resolvedOwner is null && Application.Current is not null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w != dialog && w.IsVisible && w.IsLoaded)
                {
                    resolvedOwner = w;
                    break;
                }
            }
        }

        if (resolvedOwner is not null && resolvedOwner.IsVisible && resolvedOwner.IsLoaded)
        {
            dialog.Owner = resolvedOwner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }
}
