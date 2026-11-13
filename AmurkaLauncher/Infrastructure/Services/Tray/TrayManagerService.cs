using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Platform;
using AmurkaLauncher.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Infrastructure.Services.Tray;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class TrayManagerService(ILogger<TrayManagerService>? logger = null) : ITrayManagerService
{
    private static readonly SolidColorBrush AccentTextBrush = CreateFrozenAccentBrush();

    private static SolidColorBrush CreateFrozenAccentBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
        brush.Freeze();
        return brush;
    }

    private const int WmUser = 0x0400;
    private const int WmTrayIcon = WmUser + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int NinBalloonUserClick = 0x0405;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifNosound = 0x00000010;
    private const int SwRestore = 9;

    private Window? _ownerWindow;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private IntPtr _trayIconHandle;
    private bool _trayIconAdded;
    private ContextMenu? _trayContextMenu;
    private Action? _restoreCallback;
    private Action? _exitCallback;
    private bool _hasShownMinimizeToast;
    private bool _disposed;

    public void Initialize(Window ownerWindow, Action restoreCallback, Action exitCallback)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        ArgumentNullException.ThrowIfNull(restoreCallback);
        ArgumentNullException.ThrowIfNull(exitCallback);

        if (_disposed) return;

        _ownerWindow = ownerWindow;
        _restoreCallback = restoreCallback;
        _exitCallback = exitCallback;

        var helper = new WindowInteropHelper(ownerWindow);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        BuildTrayContextMenu();
        CreateTrayIcon();
    }

    public void MinimizeToTray(bool showBalloon = true)
    {
        if (_disposed || _ownerWindow is null) return;

        _ownerWindow.Hide();
        _ownerWindow.ShowInTaskbar = false;

        if (showBalloon && !_hasShownMinimizeToast)
        {
            _hasShownMinimizeToast = true;
            ShowBalloonNotification(
                "AMURKA PVE Launcher",
                "Лаунчер свёрнут в системный трей. Нажмите ПКМ по иконке для меню.");
        }

        // Выполняем глубокий сброс рабочего набора и уплотнение LOH при сворачивании
        Task.Delay(1000).ContinueWith(_ => MemoryOptimizer.TrimWorkingSet(), TaskScheduler.Default);
    }

    public void RestoreFromTray()
    {
        if (_disposed || _ownerWindow is null) return;

        _ownerWindow.ShowInTaskbar = true;
        _ownerWindow.Show();
        if (_ownerWindow.WindowState == WindowState.Minimized)
        {
            _ownerWindow.WindowState = WindowState.Normal;
        }

        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SwRestore);
            SetForegroundWindow(_hwnd);
        }

        _ownerWindow.Activate();
        _restoreCallback?.Invoke();
    }

    public void ShowBalloonNotification(string title, string text)
    {
        if (_disposed || !_trayIconAdded || _hwnd == IntPtr.Zero) return;

        try
        {
            var data = CreateNotifyIconData();
            data.uFlags = NifInfo;
            data.szInfoTitle = title;
            data.szInfo = text;
            data.dwInfoFlags = NiifInfo | NiifNosound;
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Не удалось показать системное уведомление в трее.");
        }
    }

    private void CreateTrayIcon()
    {
        if (_hwnd == IntPtr.Zero || _trayIconAdded) return;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                _trayIconHandle = ExtractIcon(IntPtr.Zero, exePath, 0);
            }

            if (_trayIconHandle == IntPtr.Zero)
            {
                _trayIconHandle = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
            }

            var data = CreateNotifyIconData();
            data.uFlags = NifMessage | NifIcon | NifTip;
            data.uCallbackMessage = WmTrayIcon;
            data.hIcon = _trayIconHandle;
            data.szTip = $"{AppConstants.AppName} v{AppConstants.Version}";

            _trayIconAdded = Shell_NotifyIcon(NimAdd, ref data);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Ошибка создания иконки системного трея.");
        }
    }

    private void BuildTrayContextMenu()
    {
        var menu = new ContextMenu();
        if (_ownerWindow?.TryFindResource("CyberpunkTrayContextMenu") is Style menuStyle)
            menu.Style = menuStyle;

        // Фирменная шапка с логотипом проекта (icon.png)
        var headerItem = new MenuItem();
        if (_ownerWindow?.TryFindResource("CyberpunkTrayHeaderItem") is Style headerStyle)
            headerItem.Style = headerStyle;

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            var logoUri = new Uri("pack://application:,,,/Resources/icon.png", UriKind.Absolute);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = logoUri;
            bitmap.DecodePixelWidth = 32;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            headerPanel.Children.Add(new Image
            {
                Source = bitmap,
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Не удалось загрузить логотип контекстного меню трея.");
        }

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = "AMURKA PVE",
            FontWeight = FontWeights.Black,
            FontSize = 12,
            Foreground = Brushes.White
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"v{AppConstants.Version}",
            FontSize = 10,
            Foreground = AccentTextBrush
        });
        headerPanel.Children.Add(textPanel);
        headerItem.Header = headerPanel;
        menu.Items.Add(headerItem);

        // Открыть окно
        var openItem = new MenuItem { Header = "Развернуть лаунчер" };
        if (_ownerWindow?.TryFindResource("CyberpunkTrayMenuItem") is Style itemStyle)
            openItem.Style = itemStyle;
        openItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(openItem);

        var sep = new Separator();
        if (_ownerWindow?.TryFindResource("CyberpunkTraySeparator") is Style sepStyle)
            sep.Style = sepStyle;
        menu.Items.Add(sep);

        // Выход
        var exitItem = new MenuItem { Header = "Выход" };
        if (_ownerWindow?.TryFindResource("CyberpunkTrayExitMenuItem") is Style exitStyle)
            exitItem.Style = exitStyle;
        exitItem.Click += (_, _) => _exitCallback?.Invoke();
        menu.Items.Add(exitItem);

        _trayContextMenu = menu;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayIcon)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg is WmLButtonUp or WmLButtonDblClk or NinBalloonUserClick)
            {
                RestoreFromTray();
                handled = true;
            }
            else if (mouseMsg == WmRButtonUp)
            {
                ShowContextMenuAtCursor();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenuAtCursor()
    {
        if (_trayContextMenu is null || _hwnd == IntPtr.Zero) return;
        SetForegroundWindow(_hwnd);
        _trayContextMenu.Placement = PlacementMode.MousePoint;
        _trayContextMenu.IsOpen = true;
    }

    private NOTIFYICONDATA CreateNotifyIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _hwndSource?.RemoveHook(WndProc);
            if (_trayIconAdded && _hwnd != IntPtr.Zero)
            {
                var data = CreateNotifyIconData();
                Shell_NotifyIcon(NimDelete, ref data);
                _trayIconAdded = false;
            }

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ошибка освобождения ресурсов системного трея.");
        }

        _hwndSource = null;
        _ownerWindow = null;
        _trayContextMenu = null;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
