using AmurkaLauncher.Core.Domain.ValueObjects;

namespace AmurkaLauncher.Infrastructure.Abstractions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public interface IVerificationService
{
    string? GetActiveSteamId64(GamePath gamePath);
    Task SendVerificationToAllAsync(string steamId64, string launcherVersion, int installedModsCount, string serverIp, CancellationToken ct = default);
}
