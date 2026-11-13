using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Amurka.Shared.Platform;

public static class ProcessManagerHelper
{
    public static bool IsAnyProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        Process[] procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (Process p in procs) p.Dispose();
        }
    }

    public static bool TryGetRunningPeerState(
        out bool isResponding,
        out IntPtr mainWindowHandle,
        ILogger? logger = null)
    {
        isResponding = true;
        mainWindowHandle = IntPtr.Zero;

        try
        {
            using var current = Process.GetCurrentProcess();
            Process[] procs = Process.GetProcessesByName(current.ProcessName);
            try
            {
                Process? existing = procs.FirstOrDefault(p => p.Id != current.Id);
                if (existing != null && !existing.HasExited)
                {
                    isResponding = existing.Responding;
                    mainWindowHandle = existing.MainWindowHandle;
                    return true;
                }
            }
            finally
            {
                foreach (Process p in procs) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Не удалось получить состояние соседнего экземпляра процесса.");
        }

        return false;
    }

    public static void KillProcesses(
        IEnumerable<string> processNames,
        bool excludeCurrentProcess = true,
        int waitExitMs = 1500,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        int currentPid = Environment.ProcessId;

        foreach (string name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            Process[] procs = Process.GetProcessesByName(name);
            try
            {
                foreach (Process proc in procs)
                {
                    if (excludeCurrentProcess && proc.Id == currentPid)
                        continue;

                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(waitExitMs);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Не удалось принудительно завершить процесс '{ProcessName}' (PID {Pid}).", name, proc.Id);
                    }
                }
            }
            finally
            {
                foreach (Process proc in procs) proc.Dispose();
            }
        }
    }
}
