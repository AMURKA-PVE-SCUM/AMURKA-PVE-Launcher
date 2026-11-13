using Amurka.Shared.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Core.Domain.ValueObjects;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IGameLauncherService
{
    Result LaunchGame(GamePath gamePath, LaunchMode mode);
}
