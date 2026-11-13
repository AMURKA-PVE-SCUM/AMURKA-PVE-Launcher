namespace AmurkaLauncher.Infrastructure.Abstractions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public interface IProcessSupervisor
{
    bool IsGameRunning();
    void KillGameProcesses();
    Task<bool> WaitForGameExitAsync(CancellationToken ct = default);
}
