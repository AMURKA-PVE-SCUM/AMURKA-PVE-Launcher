using System.Diagnostics;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Infrastructure.Abstractions;

namespace AmurkaLauncher.Infrastructure.Services.Game.Strategies;

public class VulkanLaunchStrategy : ILaunchStrategy
{
    public LaunchMode Mode => LaunchMode.Vulkan;

    public void EnrichProcessStartInfo(ProcessStartInfo startInfo)
    {
        string[] baseArgs = ["-nobattleye", "-skipfilestreamingchecks", "-nohmd", "-nosplash", "-fileopenlog", "-vulkan"];
        foreach (var arg in baseArgs)
        {
            if (!startInfo.ArgumentList.Contains(arg))
                startInfo.ArgumentList.Add(arg);
        }
    }
}
