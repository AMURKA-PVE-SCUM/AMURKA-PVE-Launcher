using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace AmurkaLauncher.Presentation.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected static Dispatcher UiDispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    protected static void RunOnUi(Action action)
    {
        if (UiDispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            UiDispatcher.BeginInvoke(action);
        }
    }
}
