using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldCollision;

namespace Mmorpg.SimulationWorker.Registry;

public sealed class SimulationWorkerHeartbeatService(
    GameServiceClient gameServiceClient,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    ActiveSimulationSessionStore sessionStore,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld collisionWorld,
    ILogger<SimulationWorkerHeartbeatService> logger,
    SimulationWorkerRegistrationLease? registrationLease = null,
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
{
    private const string WorkerRuntimeChangedCode = "worker_runtime_changed";

    private volatile bool registrationAttempted;
    private volatile bool hasHeldAuthority;
    private long? acquireStartedTimestamp;

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

        using var timeout = new CancellationTokenSource(config.GameServiceTimeout);
        var result = await gameServiceClient.MarkSimulationWorkerOfflineAsync(
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
            // A runtime that never owned the worker id has no authority to lose.
            if (hasHeldAuthority && IsAuthorityFailure(result.Error.Code))
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
        var renewalStart = registrationLease?.CaptureRenewalStart();
        var result = await gameServiceClient.HeartbeatSimulationWorkerAsync(
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
            if (IsWaitingForPreviousRuntime(result.Error.Code))
            {
                return;
            }

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

        if (renewalStart is not null)
        {
            registrationLease!.Renew(
                renewalStart.Value,
                result.Value.OnlineUntil - result.Value.LastHeartbeatAt);
        }

        hasHeldAuthority = true;
        acquireStartedTimestamp = null;
    }

    // A replacement runtime can start before the previous runtime's lease expires, and
    // the registry orders two live runtimes by reported start time, which a slower host
    // clock loses. Until this runtime first owns the worker id it keeps retrying, while
    // the realtime transport rejects every connection, and only gives up when another
    // runtime stays live for longer than the acquire timeout.
    private bool IsWaitingForPreviousRuntime(string code)
    {
        if (hasHeldAuthority
            || !string.Equals(code, WorkerRuntimeChangedCode, StringComparison.Ordinal))
        {
            return false;
        }

        acquireStartedTimestamp ??= Stopwatch.GetTimestamp();
        var waited = Stopwatch.GetElapsedTime(acquireStartedTimestamp.Value);
        if (waited < config.RegistryAcquireTimeout)
        {
            logger.LogWarning(
                "Simulation worker {WorkerId} runtime {RuntimeId} is waiting for the previous runtime's registry lease to expire ({Waited} of {Timeout}).",
                config.SimulationWorkerId,
                identity.RuntimeId,
                waited,
                config.RegistryAcquireTimeout);
            return true;
        }

        logger.LogCritical(
            "Simulation worker {WorkerId} runtime {RuntimeId} could not acquire the worker id within {Timeout}, so another live runtime owns it.",
            config.SimulationWorkerId,
            identity.RuntimeId,
            config.RegistryAcquireTimeout);
        return false;
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
