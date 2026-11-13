using Amurka.Shared.Platform;
using AmurkaLauncher.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Infrastructure.Services.Game;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ProcessSupervisor(ILogger<ProcessSupervisor>? logger = null) : IProcessSupervisor
{
    private static readonly string[] ScumProcessNames = ["SCUM", "SCUM-Win64-Shipping", "SCUM_Launcher"];

    public bool IsGameRunning() =>
        ScumProcessNames.Any(ProcessManagerHelper.IsAnyProcessRunning);

    public void KillGameProcesses() =>
        ProcessManagerHelper.KillProcesses(ScumProcessNames, excludeCurrentProcess: true, logger: logger);

    public async Task<bool> WaitForGameExitAsync(CancellationToken ct = default)
    {
        while (IsGameRunning())
        {
            if (ct.IsCancellationRequested) return false;
            await Task.Delay(500, ct);
        }
        return true;
    }
}
