using System.Windows;

namespace AmurkaLauncher.Infrastructure.Abstractions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public interface ITrayManagerService : IDisposable
{
    void Initialize(Window ownerWindow, Action restoreCallback, Action exitCallback);
    void ShowBalloonNotification(string title, string text);
    void MinimizeToTray(bool showBalloon = true);
    void RestoreFromTray();
}
