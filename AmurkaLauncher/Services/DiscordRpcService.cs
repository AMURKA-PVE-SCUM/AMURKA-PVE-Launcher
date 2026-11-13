using System.IO;
using Amurka.Shared.Core.Constants;
using Amurka.Shared.Platform;
using AmurkaLauncher.Infrastructure.Abstractions;
using DiscordRPC;
using Microsoft.Extensions.Logging;

namespace AmurkaLauncher.Services;

public sealed class DiscordRpcService(
    TimeProvider? timeProvider = null,
    ILogger<DiscordRpcService>? logger = null) : IDiscordRpcService
{
    private static readonly string[] DiscordProcessNames = ["Discord", "DiscordCanary", "DiscordPTB"];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DiscordRpcClient? _client;
    private bool _disposed;

    public void Initialize()
    {
        if (_disposed) return;

        // Если Discord не запущен в системе, выходим без создания фоновых потоков и аллокаций
        if (!IsDiscordRunning()) return;

        try
        {
            _client = new DiscordRpcClient(AppConstants.DiscordClientId);
            _client.Initialize();
            _client.SetPresence(new RichPresence
            {
                Details = "AMURKA PVE",
                State = AppConstants.ServerAddress,
                Timestamps = new Timestamps(_timeProvider.GetUtcNow().UtcDateTime),
                Assets = new Assets
                {
                    LargeImageKey = "AMURKA",
                    LargeImageText = AppConstants.ServerAddress
                }
            });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Не удалось инициализировать Discord Rich Presence.");
            try
            {
                _client?.Dispose();
            }
            catch (Exception disposeEx)
            {
                logger?.LogDebug(disposeEx, "Ошибка освобождения DiscordRpcClient после сбоя.");
            }
            _client = null;
        }
    }

    private static bool IsDiscordRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;

        // 1. Быстрая проверка наличия активных именованных каналов Discord IPC
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists($@"\\.\pipe\discord-ipc-{i}")) return true;
        }

        // 2. Fallback: проверка запущенных процессов Discord с гарантированным освобождением дескрипторов
        return DiscordProcessNames.Any(ProcessManagerHelper.IsAnyProcessRunning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _client?.Deinitialize();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ошибка деинициализации Discord Rich Presence.");
        }
        finally
        {
            _client = null;
        }

        GC.SuppressFinalize(this);
    }
}
