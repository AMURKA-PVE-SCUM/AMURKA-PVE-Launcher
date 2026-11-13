namespace AmurkaInstaller.Infrastructure.Abstractions;

public interface IShortcutManager
{
    void CreateDesktopShortcut(string installDir);
    void CreateStartMenuShortcuts(string installDir);
    void RemoveShortcuts();
}
