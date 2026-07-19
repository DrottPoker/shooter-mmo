using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Realtime;
using SimulationWorker.Sessions;
using SimulationWorker.WorldCollision;

namespace SimulationWorker.Registry;

public sealed class SimulationWorkerHeartbeatService(
    AuthServiceClient authServiceClient,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    ActiveSimulationSessionStore sessionStore,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld collisionWorld,
    ILogger<SimulationWorkerHeartbeatService> logger,
    SimulationWorkerRegistrationLease? registrationLease = null,
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
{
    private volatile bool registrationAttempted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await transportReadiness.WaitUntilListeningAsync(stoppingToken);
        await SendHeartbeatAsync(stoppingToken);
        using var timer = new PeriodicTimer(config.RegistryHeartbeatInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SendHeartbeatAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        registrationLease?.Clear();
        await base.StopAsync(cancellationToken);
        if (!registrationAttempted)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(config.AuthServiceTimeout);
        var result = await authServiceClient.MarkSimulationWorkerOfflineAsync(
            config.SimulationWorkerId,
            identity.RuntimeId,
            timeout.Token);
        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Simulation worker {WorkerId} runtime {RuntimeId} could not be marked offline: {Code}.",
                config.SimulationWorkerId,
                identity.RuntimeId,
                result.Error!.Code);
            if (IsAuthorityFailure(result.Error.Code))
            {
                StopForLostAuthority(result.Error.Code);
            }
            return;
        }

        logger.LogInformation(
            "Simulation worker {WorkerId} runtime {RuntimeId} was marked offline.",
            config.SimulationWorkerId,
            identity.RuntimeId);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        registrationAttempted = true;
        var result = await authServiceClient.HeartbeatSimulationWorkerAsync(
            config.SimulationWorkerId,
            new SimulationWorkerHeartbeatRequest(
                config.FleetId,
                config.NodeId,
                config.ShardId,
                config.WorldId,
                identity.RuntimeId,
                identity.StartedAt,
                config.AdvertisedHost,
                config.AdvertisedUdpPort,
                config.MaxConnections,
                sessionStore.ListActiveSessions().Count,
                RealtimeProtocol.Version,
                GameSimulationCompatibility.Revision,
                collisionWorld.Revision),
            cancellationToken);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Simulation worker registry heartbeat failed for {WorkerId} runtime {RuntimeId}: {Code}.",
                config.SimulationWorkerId,
                identity.RuntimeId,
                result.Error!.Code);
            if (IsAuthorityFailure(result.Error.Code))
            {
                StopForLostAuthority(result.Error.Code);
            }

            return;
        }

        if (!string.Equals(result.Value!.ShardId, config.ShardId, StringComparison.Ordinal)
            || !string.Equals(result.Value.WorldId, config.WorldId, StringComparison.Ordinal))
        {
            logger.LogError(
                "Simulation worker registry returned assignment {ShardId}/{WorldId}, expected {ExpectedShardId}/{ExpectedWorldId}.",
                result.Value.ShardId,
                result.Value.WorldId,
                config.ShardId,
                config.WorldId);
            StopForLostAuthority("simulation_assignment_mismatch");
            return;
        }

        registrationLease?.Renew(result.Value.OnlineUntil - result.Value.LastHeartbeatAt);
    }

    private void StopForLostAuthority(string code)
    {
        registrationLease?.Clear();
        logger.LogCritical(
            "Simulation worker {WorkerId} runtime {RuntimeId} lost registry authority with code {Code}. Stopping the process.",
            config.SimulationWorkerId,
            identity.RuntimeId,
            code);
        applicationLifetime?.StopApplication();
    }

    private static bool IsAuthorityFailure(string code)
    {
        return code is "shard_assignment_conflict"
            or "shard_world_rebind_blocked"
            or "worker_runtime_changed"
            or "simulation_topology_mismatch"
            or "simulation_assignment_target_not_found"
            or "simulation_world_not_found"
            or "invalid_simulation_worker_identity"
            or "invalid_worker_started_at"
            or "invalid_worker_host"
            or "invalid_worker_port"
            or "invalid_worker_capacity"
            or "invalid_protocol_version"
            or "invalid_worker_revision"
            or "service_worker_mismatch"
            or "auth_service_authentication_failed";
    }
}
