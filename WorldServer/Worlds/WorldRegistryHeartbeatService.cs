using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorldServer.Auth;
using WorldServer.Config;

namespace WorldServer.Worlds;

public sealed class WorldRegistryHeartbeatService(
    AuthServiceClient authServiceClient,
    WorldServerConfig config,
    ILogger<WorldRegistryHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SendHeartbeatAsync(stoppingToken);
        using var timer = new PeriodicTimer(config.WorldRegistryHeartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SendHeartbeatAsync(stoppingToken);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var result = await authServiceClient.HeartbeatWorldAsync(
            config.WorldServerId,
            cancellationToken);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "World registry heartbeat failed for {WorldId}: {Code}.",
                config.WorldServerId,
                result.Error!.Code);
        }
    }
}
