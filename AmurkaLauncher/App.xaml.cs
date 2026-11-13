using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Logging;
using Amurka.Shared.Platform;
using Amurka.Shared.UI.Controls;
using AmurkaInstaller;
using AmurkaInstaller.Core.Domain.Models;
using AmurkaInstaller.Infrastructure.Abstractions;
using AmurkaInstaller.Infrastructure.Services;
using AmurkaInstaller.Presentation.ViewModels;
using AmurkaLauncher.Infrastructure.Services.Tray;
using Microsoft.Extensions.Logging;
using AmurkaLauncher.Infrastructure.Abstractions;
using AmurkaLauncher.Infrastructure.Services.Game;
using AmurkaLauncher.Infrastructure.Services.Game.Strategies;
using AmurkaLauncher.Infrastructure.Services.Maintenance;
using AmurkaLauncher.Presentation.ViewModels;
using AmurkaLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AmurkaLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private const string MutexName = @"Local\AmurkaLauncher_SingleInstance_Mutex_DOMO";
    private const string WakeupEventName = @"Local\AmurkaLauncher_Wakeup_Event_DOMO";
    private static Mutex? _singleInstanceMutex;
    private static bool _hasHandle;
    private static EventWaitHandle? _wakeupEvent;
    private static RegisteredWaitHandle? _registeredWait;

    public static readonly int WmShowLauncher = RegisterWindowMessage("AMURKA_LAUNCHER_ACTIVATE");

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilter(int msg, uint dwFlag);

    private const uint MsgfltAdd = 1;
    private const int SwRestore = 9;
    private const int HwndBroadcast = 0xffff;

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

        // 2. Проверка режима установщика / обновления / удаления (Single-Binary Self-Installer)
        if (IsInstallerMode(e.Args))
        {
            RunInstallerMode(e.Args);
            return;
        }

        // 3. Безопасная проверка единственного экземпляра
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out _hasHandle);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущий процесс аварийно завершился, перехватываем владение мьютексом
            _hasHandle = true;
        }
        catch (Exception ex)
        {
            LogCrash("MutexInitialization", ex);
            _hasHandle = true;
        }

        if (!_hasHandle)
        {
            // 1. Посылаем сигнал пробуждения через именованный EventWaitHandle (срабатывает мгновенно даже если окно в трее/скрыто)
            try
            {
                using var wakeup = EventWaitHandle.OpenExisting(WakeupEventName);
                wakeup.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Второй процесс может запускаться до создания события
            }
            catch (Exception ex)
            {
                LogCrash("WakeupSignal", ex);
            }

            // 2. Дополнительно отправляем системное сообщение окну
            PostMessage((IntPtr)HwndBroadcast, WmShowLauncher, IntPtr.Zero, IntPtr.Zero);

            bool isProcessAlive = ProcessManagerHelper.TryGetRunningPeerState(out bool isProcessResponding, out IntPtr mainWindowHandle);
            if (isProcessAlive && mainWindowHandle != IntPtr.Zero)
            {
                ShowWindowAsync(mainWindowHandle, SwRestore);
                SetForegroundWindow(mainWindowHandle);
            }

            // Если запущенный экземпляр найден и отвечает — сигнал пробуждения доставлен, тихо завершаем второй экземпляр
            if (isProcessAlive && isProcessResponding)
            {
                Shutdown();
                return;
            }

            // Только если экземпляр реально завис (Responding == false) — предлагаем пользователю завершить его
            if (isProcessAlive && !isProcessResponding)
            {
                var answer = AmurkaMessageBox.Show(
                    "Обнаружен зависший процесс AMURKA PVE Launcher в памяти.\n\nЗавершить его и запустить лаунчер заново?",
                    "AMURKA PVE Launcher",
                    AmurkaDialogButtons.YesNo,
                    AmurkaDialogIcon.Question,
                    confirmText: "Да",
                    cancelText: "Отмена"
                );

                if (answer == true)
                {
                    try
                    {
                        using var current = Process.GetCurrentProcess();
                        ProcessManagerHelper.KillProcesses([current.ProcessName], excludeCurrentProcess: true);

                        StartLauncherApplication(e);
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

        StartLauncherApplication(e);
    }

    private void StartLauncherApplication(StartupEventArgs e)
    {
        // Разрешаем широковещательное сообщение активации через UIPI
        ChangeWindowMessageFilter(WmShowLauncher, MsgfltAdd);

        // Инициализируем именованное событие пробуждения из системного трея
        try
        {
            _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeupEventName);
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _wakeupEvent,
                (state, timedOut) =>
                {
                    if (!timedOut)
                    {
                        Current?.Dispatcher.BeginInvoke(() =>
                        {
                            foreach (Window w in Current.Windows)
                            {
                                if (w is MainWindow mw)
                                {
                                    mw.RestoreFromTray();
                                    return;
                                }
                            }
                        });
                    }
                },
                null,
                -1,
                false);
        }
        catch (Exception ex)
        {
            LogCrash("WakeupEventInit", ex);
        }

        // Отключаем трассировку привязок данных WPF для устранения накладных расходов форматирования и работы со строками
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;

        base.OnStartup(e);

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<IProcessSupervisor, ProcessSupervisor>();
        services.AddSingleton<IScumDetectionService, ScumDetectionService>();
        services.AddSingleton<IConfigRepository, ConfigService>();
        services.AddSingleton<IDiscordRpcService, DiscordRpcService>();
        services.AddSingleton<IVerificationService, LauncherVerificationService>();
        services.AddLogging(builder =>
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "amurka-launcher", "logs", "launcher.log");
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions
            {
                LogFilePath = logPath,
                MaxFileSizeBytes = 5 * 1024 * 1024,
                MaxArchiveFiles = 3
            }));
        });

        services.AddSingleton<ITrayManagerService, TrayManagerService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IClientMaintenanceService, ClientMaintenanceService>();

        // Mod Management Engine
        services.AddSingleton<ModManagerService>();
        services.AddSingleton<IModSyncEngine>(sp => sp.GetRequiredService<ModManagerService>());
        services.AddSingleton<IModCatalogRepository>(sp => sp.GetRequiredService<ModManagerService>());
        services.AddSingleton<IInstalledStateRepository>(sp => sp.GetRequiredService<ModManagerService>());

        // Game Launch Strategies
        services.AddSingleton<ILaunchStrategy, Dx11LaunchStrategy>();
        services.AddSingleton<ILaunchStrategy, Dx12LaunchStrategy>();
        services.AddSingleton<ILaunchStrategy, VulkanLaunchStrategy>();
        services.AddSingleton<IGameLauncherService, GameLauncherService>();

        // ViewModels
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ModSyncViewModel>();
        services.AddSingleton<UpdateModalViewModel>();
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _registeredWait?.Unregister(null);
            _wakeupEvent?.Dispose();
        }
        catch (Exception ex)
        {
            LogCrash("OnExitWakeupCleanup", ex);
        }

        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_hasHandle && _singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                LogCrash("ReleaseMutex", ex);
            }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static bool IsInstallerMode(string[] args)
    {
        string? procPath = Environment.ProcessPath;
        string? fileName = !string.IsNullOrEmpty(procPath) ? Path.GetFileName(procPath) : null;

        if (fileName != null && (fileName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                                 fileName.Contains("Install", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string arg in args)
        {
            if (arg.Equals("/INSTALL", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-install", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/SETUP", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/UPDATE", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--update", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async void RunInstallerMode(string[] args)
    {
        var options = InstallerOptions.Parse(args);
        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddLogging(builder =>
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AMURKA-PVE-Setup", "logs", "installer.log");
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions
            {
                LogFilePath = logPath,
                MaxFileSizeBytes = 2 * 1024 * 1024,
                MaxArchiveFiles = 2
            }));
        });

        services.AddSingleton<IPayloadExtractor, PayloadExtractor>();
        services.AddSingleton<IShortcutManager, ShortcutManager>();
        services.AddSingleton<IInstallerRegistryService, InstallerRegistryService>();
        services.AddSingleton<IInstallerProcessManager, InstallerProcessManager>();
        services.AddSingleton<IInstallLocationDetector, InstallLocationDetector>();
        services.AddTransient<InstallerViewModel>();
        services.AddTransient<InstallerWindow>();

        var sp = services.BuildServiceProvider();
        Services = sp;

        if (options.IsSilent)
        {
            try
            {
                var vm = sp.GetRequiredService<InstallerViewModel>();
                await vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                LogCrash("SilentInstallerExecution", ex);
            }
            finally
            {
                Shutdown(0);
            }
            return;
        }

        var installerWindow = sp.GetRequiredService<InstallerWindow>();
        installerWindow.Show();
    }

    private static void LogCrash(string source, Exception? ex)
        => CrashLogger.Log(CrashLogPath, source, ex);
}
