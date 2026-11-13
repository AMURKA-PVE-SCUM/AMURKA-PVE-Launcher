namespace AmurkaInstaller.Infrastructure.Abstractions;

public interface IInstallerProcessManager
{
    Task WaitForLauncherExitAsync(TimeSpan timeout, string? installDir = null, CancellationToken ct = default);
    void KillRunningLauncherProcesses(string? installDir = null);
    void LaunchInstalledApp(string installDir);
    void ScheduleSelfDirectoryDeletion(string installDir);
}
