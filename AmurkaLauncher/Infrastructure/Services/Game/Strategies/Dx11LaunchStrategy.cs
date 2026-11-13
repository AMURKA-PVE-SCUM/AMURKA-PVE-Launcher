using System.Diagnostics;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Infrastructure.Abstractions;

namespace AmurkaLauncher.Infrastructure.Services.Game.Strategies;

public class Dx11LaunchStrategy : ILaunchStrategy
{
    public LaunchMode Mode => LaunchMode.Dx11;

    public void EnrichProcessStartInfo(ProcessStartInfo startInfo)
    {
        // Базовые аргументы оптимизации SCUM
        string[] baseArgs = ["-nobattleye", "-skipfilestreamingchecks", "-nohmd", "-nosplash", "-fileopenlog"];
        foreach (var arg in baseArgs)
        {
            if (!startInfo.ArgumentList.Contains(arg))
                startInfo.ArgumentList.Add(arg);
        }
    }
}
