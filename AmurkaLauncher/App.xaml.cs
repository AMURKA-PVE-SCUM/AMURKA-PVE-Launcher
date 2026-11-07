using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AmurkaLauncher.Controls;

namespace AmurkaLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\AmurkaLauncher_SingleInstance_Mutex_DOMO";
    private static Mutex? _singleInstanceMutex;
    private static bool _hasHandle;

    public static readonly int WmShowLauncher = RegisterWindowMessage("AMURKA_LAUNCHER_ACTIVATE");

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;
    public const int HWND_BROADCAST = 0xffff;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "amurka-launcher",
        "crash.log"
    );

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out _hasHandle);

        if (!_hasHandle)
        {
            // Уведомляем уже запущенный экземпляр развернуть окно
            PostMessage((IntPtr)HWND_BROADCAST, WmShowLauncher, IntPtr.Zero, IntPtr.Zero);

            // Дополнительно ищем окно процесса и выводим на передний план напрямую
            try
            {
                var current = Process.GetCurrentProcess();
                var existing = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id);

                if (existing != null && existing.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(existing.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(existing.MainWindowHandle);
                }
            }
            catch { }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            AmurkaMessageBox.Show(
                $"Произошла ошибка при работе программы:\n\n{args.Exception?.Message}\n\nПодробности записаны в crash.log",
                "AMURKA PVE Launcher",
                AmurkaDialogButtons.Ok,
                AmurkaDialogIcon.Error
            );
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hasHandle && _singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string? dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex?.ToString()}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath, log);
        }
        catch { }
    }
}
