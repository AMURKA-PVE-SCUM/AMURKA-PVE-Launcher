using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Amurka.Shared.Core.Domain.Results;
using AmurkaLauncher.Core.Domain.Enums;
using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Infrastructure.Abstractions;

namespace AmurkaLauncher.Services;

public sealed class GameLauncherService : IGameLauncherService
{
    private readonly FrozenDictionary<LaunchMode, ILaunchStrategy> _strategies;
    private readonly ILaunchStrategy _fallbackStrategy;

    public GameLauncherService(IEnumerable<ILaunchStrategy> launchStrategies)
    {
        _strategies = launchStrategies.ToFrozenDictionary(s => s.Mode);
        _fallbackStrategy = _strategies.GetValueOrDefault(LaunchMode.Dx11) ?? _strategies.Values.First();
    }

    public GameLauncherService() : this([
        new Infrastructure.Services.Game.Strategies.Dx11LaunchStrategy(),
        new Infrastructure.Services.Game.Strategies.Dx12LaunchStrategy(),
        new Infrastructure.Services.Game.Strategies.VulkanLaunchStrategy()
    ])
    {
    }

    public Result LaunchGame(GamePath gamePath, LaunchMode mode)
    {
        string? exePath = gamePath.FindExecutable();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return Result.Failure("Исполняемый файл SCUM.exe не найден");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = false
            };

            var strategy = _strategies.GetValueOrDefault(mode, _fallbackStrategy);
            strategy.EnrichProcessStartInfo(startInfo);

            try
            {
                Process.Start(startInfo);
            }
            catch (Win32Exception winEx) when (winEx.NativeErrorCode == 740)
            {
                // Fallback с системным шеллом для запроса повышения прав UAC
                var elevatedStartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    UseShellExecute = true,
                    Arguments = string.Join(' ', startInfo.ArgumentList)
                };
                Process.Start(elevatedStartInfo);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Ошибка запуска: {ex.Message}");
        }
    }

    public void LaunchGame(string exePath, string mode)
    {
        var result = LaunchGame(GamePath.From(Path.GetDirectoryName(exePath)), LaunchModeExtensions.FromKey(mode));
        if (result.IsFailure)
        {
            throw new FileNotFoundException(result.Error, exePath);
        }
    }
}
