using Amurka.Shared.Core.Domain.Results;

namespace AmurkaLauncher.Infrastructure.Abstractions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public interface IClientMaintenanceService
{
    Task<Result<(bool wasEmpty, int deletedFiles)>> WipeClientCacheAsync(CancellationToken ct = default);
}
