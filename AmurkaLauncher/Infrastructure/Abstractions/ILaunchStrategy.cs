using System.Collections.ObjectModel;
using System.Diagnostics;
using AmurkaLauncher.Core.Domain.Enums;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface ILaunchStrategy
{
    LaunchMode Mode { get; }
    void EnrichProcessStartInfo(ProcessStartInfo startInfo);
}
