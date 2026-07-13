using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Realtime;
using WorldServer.WorldCollision;

namespace WorldServer.Worlds;

public sealed class WorldRegistryHeartbeatService(
    AuthServiceClient authServiceClient,
    WorldServerConfig config,
    WorldServerInstanceIdentity instanceIdentity,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld collisionWorld,
    ILogger<WorldRegistryHeartbeatService> logger) : BackgroundService
{
    private volatile bool registrationAttempted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await transportReadiness.WaitUntilListeningAsync(stoppingToken);
        await SendHeartbeatAsync(stoppingToken);
        using var timer = new PeriodicTimer(config.WorldRegistryHeartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SendHeartbeatAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!registrationAttempted)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(config.AuthServiceTimeout);
        var result = await authServiceClient.MarkWorldOfflineAsync(
            config.WorldServerId,
            instanceIdentity.InstanceId,
            timeout.Token);
        if (!result.Succeeded)
        {
            logger.LogWarning(
                "World registry offline update failed for {WorldId} instance {InstanceId}: {Code}.",
                config.WorldServerId,
                instanceIdentity.InstanceId,
                result.Error!.Code);
            return;
        }

        logger.LogInformation(
            "World {WorldId} instance {InstanceId} was marked offline.",
            config.WorldServerId,
            instanceIdentity.InstanceId);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        registrationAttempted = true;
        var result = await authServiceClient.HeartbeatWorldAsync(
            config.WorldServerId,
            new WorldHeartbeatRequest(
                config.AdvertisedHost,
                config.AdvertisedUdpPort,
                instanceIdentity.InstanceId,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision,
                collisionWorld.Revision),
            cancellationToken);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "World registry heartbeat failed for {WorldId}: {Code}.",
                config.WorldServerId,
                result.Error!.Code);
            return;
        }

    }
}
