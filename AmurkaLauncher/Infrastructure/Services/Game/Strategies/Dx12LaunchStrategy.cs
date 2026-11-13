using System.Diagnostics;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Infrastructure.Abstractions;

namespace AmurkaLauncher.Infrastructure.Services.Game.Strategies;

public class Dx12LaunchStrategy : ILaunchStrategy
{
    public LaunchMode Mode => LaunchMode.Dx12;

    public void EnrichProcessStartInfo(ProcessStartInfo startInfo)
    {
        string[] baseArgs = ["-nobattleye", "-skipfilestreamingchecks", "-nohmd", "-nosplash", "-fileopenlog", "-dx12"];
        foreach (var arg in baseArgs)
        {
            if (!startInfo.ArgumentList.Contains(arg))
                startInfo.ArgumentList.Add(arg);
        }
    }
}
