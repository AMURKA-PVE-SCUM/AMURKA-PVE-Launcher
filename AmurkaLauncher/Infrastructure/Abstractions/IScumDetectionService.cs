using Amurka.Shared.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.ValueObjects;

namespace AmurkaLauncher.Infrastructure.Abstractions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public interface IScumDetectionService
{
    Task<Result<GamePath>> AutoDetectAsync(CancellationToken ct = default);
    GamePath NormalizeGameRoot(string path);
    ModsPath GetModsPath(GamePath gamePath);
    bool ResolveAndEnsurePaths(ref GamePath gamePath, ref ModsPath modsPath);
    string? FindScumExe(GamePath gamePath);
}
