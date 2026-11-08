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
    private const string MutexName = @"Local\AmurkaLauncher_SingleInstance_Mutex_DOMO";
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
        // 1. Инициализация глобальных обработчиков ошибок в самом начале работы
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            try
            {
                AmurkaMessageBox.Show(
                    $"Произошла непредвиденная ошибка при работе программы:\n\n{args.Exception?.Message}\n\nПодробности записаны в crash.log",
                    "AMURKA PVE Launcher",
                    AmurkaDialogButtons.Ok,
                    AmurkaDialogIcon.Error
                );
            }
            catch
            {
                MessageBox.Show(
                    $"Произошла непредвиденная ошибка:\n{args.Exception?.Message}\n\nПодробности записаны в crash.log",
                    "AMURKA PVE Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // 2. Безопасная проверка единственного экземпляра
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out _hasHandle);
        }
        catch (Exception ex)
        {
            LogCrash("MutexInitialization", ex);
            _hasHandle = true;
        }

        if (!_hasHandle)
        {
            // Уведомляем уже запущенный экземпляр развернуть окно
            PostMessage((IntPtr)HWND_BROADCAST, WmShowLauncher, IntPtr.Zero, IntPtr.Zero);

            bool windowActivated = false;
            try
            {
                var current = Process.GetCurrentProcess();
                var existing = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id);

                if (existing != null && existing.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(existing.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(existing.MainWindowHandle);
                    windowActivated = true;
                }
            }
            catch { }

            if (!windowActivated)
            {
                // Окно не найдено или завис старый фоновый процесс
                var answer = MessageBox.Show(
                    "Обнаружен предыдущий или зависший процесс AMURKA PVE Launcher в памяти.\n\nЗавершить его и запустить лаунчер заново?",
                    "AMURKA PVE Launcher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        var current = Process.GetCurrentProcess();
                        foreach (var p in Process.GetProcessesByName(current.ProcessName))
                        {
                            if (p.Id != current.Id)
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(1500);
                            }
                        }

                        base.OnStartup(e);
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogCrash("KillExistingProcess", ex);
                    }
                }
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
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
