namespace AmurkaLauncher.Core.Domain.Enums;

public enum SyncStatus
{
    Checking,
    Synced,
    UpdatesAvailable,
    UntrackedFilesFound,
    Downloading,
    Error
}
