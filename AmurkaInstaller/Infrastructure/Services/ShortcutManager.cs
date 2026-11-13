using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AmurkaInstaller.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaInstaller.Infrastructure.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ShortcutManager(ILogger<ShortcutManager>? logger = null) : IShortcutManager
{
    public void CreateDesktopShortcut(string installDir)
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktopDir, "AMURKA PVE Launcher.lnk");
            string targetExe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            CreateShortcut(shortcutPath, targetExe, "Запуск AMURKA PVE Launcher для SCUM");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Не удалось создать ярлык на рабочем столе.");
        }
    }

    public void CreateStartMenuShortcuts(string installDir)
    {
        try
        {
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AMURKA PVE Launcher");
            Directory.CreateDirectory(startMenuDir);

            string targetExe = Path.Combine(installDir, "AMURKA PVE Launcher.exe");
            string uninstallerExe = Path.Combine(installDir, "uninstall.exe");

            CreateShortcut(Path.Combine(startMenuDir, "AMURKA PVE Launcher.lnk"), targetExe, "AMURKA PVE Launcher");
            if (File.Exists(uninstallerExe))
            {
                CreateShortcut(Path.Combine(startMenuDir, "Удалить AMURKA PVE Launcher.lnk"), uninstallerExe, "Удалить AMURKA PVE Launcher");
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Не удалось создать ярлыки в меню «Пуск».");
        }
    }

    public void RemoveShortcuts()
    {
        try
        {
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AMURKA PVE Launcher.lnk");
            if (File.Exists(desktopShortcut))
            {
                try
                {
                    File.Delete(desktopShortcut);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogDebug(ex, "Не удалось удалить ярлык рабочего стола '{ShortcutPath}'.", desktopShortcut);
                }
            }

            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "AMURKA PVE Launcher");
            if (Directory.Exists(startMenuDir))
            {
                try
                {
                    Directory.Delete(startMenuDir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogDebug(ex, "Не удалось удалить папку меню «Пуск» '{StartMenuDir}'.", startMenuDir);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ошибка при удалении ярлыков.");
        }
    }

    private void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            link.SetPath(targetPath);
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                link.SetWorkingDirectory(dir);
            }
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);

            if (link is IPersistFile persistFile)
            {
                persistFile.Save(shortcutPath, false);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ошибка IShellLinkW для '{ShortcutPath}', пробуем fallback WScript.Shell.", shortcutPath);
            CreateShortcutFallback(shortcutPath, targetPath, description);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    private void CreateShortcutFallback(string shortcutPath, string targetPath, string description)
    {
        object? shellObj = null;
        object? shortcutObj = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                shellObj = Activator.CreateInstance(shellType);
                if (shellObj != null)
                {
                    dynamic shell = shellObj;
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcutObj = shortcut;
                    shortcut.TargetPath = targetPath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                    shortcut.Description = description;
                    shortcut.IconLocation = targetPath + ",0";
                    shortcut.Save();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Fallback WScript.Shell также не смог создать ярлык '{ShortcutPath}'.", shortcutPath);
        }
        finally
        {
            if (shortcutObj is not null && Marshal.IsComObject(shortcutObj))
                Marshal.FinalReleaseComObject(shortcutObj);
            if (shellObj is not null && Marshal.IsComObject(shellObj))
                Marshal.FinalReleaseComObject(shellObj);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
