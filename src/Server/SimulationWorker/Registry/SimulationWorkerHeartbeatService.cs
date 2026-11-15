using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldCollision;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Registry;

// Registers the worker runtime and renews its registration every RegistryHeartbeatInterval. The
// heartbeat is also the probe of GameServiceAvailability: one that times out, cannot reach
// GameService, or gets a server error goes again every RegistryHeartbeatRetryDelay until one
// succeeds, and another GameService call that fails that way asks for the next heartbeat at once.
// A definitive authority failure stops the worker at once; GameServiceOutageMonitor stops it when
// no heartbeat succeeded for the outage tolerance.
public sealed class SimulationWorkerHeartbeatService(
    GameServiceClient gameServiceClient,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    ActiveSimulationSessionStore sessionStore,
    RealtimeTransportReadiness transportReadiness,
    ChunkedStaticCollisionWorld collisionWorld,
    ItemCatalogRuntimeDocument itemCatalog,
    QuestCatalogRuntimeDocument questCatalog,
    CombatRules combatRules,
    SimulationShardRules shardRules,
    ILogger<SimulationWorkerHeartbeatService> logger,
    SimulationWorkerRegistrationLease? registrationLease = null,
    IHostApplicationLifetime? applicationLifetime = null,
    GameServiceAvailability? availability = null,
    SimulationWorkerStopReason? stopReason = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const string WorkerRuntimeChangedCode = "worker_runtime_changed";
    private const string LostAuthorityMessage = "The shard lost its authority and is shutting down.";

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private volatile bool registrationAttempted;
    private volatile bool hasHeldAuthority;
    private long? acquireStartedTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await transportReadiness.WaitUntilListeningAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            var outcome = await SendHeartbeatAsync(stoppingToken);
            await WaitForNextHeartbeatAsync(
                started,
                outcome == HeartbeatOutcome.Transient
                    ? config.RegistryHeartbeatRetryDelay
                    : config.RegistryHeartbeatInterval,
                stoppingToken);
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

        // Also during an outage: one attempt, which frees the Shard at once when GameService
        // answers again by now.
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

    // Waits out the delay from the start of the last heartbeat. A probe request ends the wait
    // early, but no sooner than one retry delay after that start.
    private async Task WaitForNextHeartbeatAsync(long started, TimeSpan delay, CancellationToken cancellationToken)
    {
        var earliest = delay < config.RegistryHeartbeatRetryDelay ? delay : config.RegistryHeartbeatRetryDelay;
        await DelayUntilAsync(started, earliest, cancellationToken);
        var remaining = delay - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        if (availability is null)
        {
            await Task.Delay(remaining, time, cancellationToken);
            return;
        }

        using var waitEnded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waited = Task.Delay(remaining, time, waitEnded.Token);
        await Task.WhenAny(waited, availability.WaitForProbeRequestAsync());
        await waitEnded.CancelAsync();
        try
        {
            await waited;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A probe request ended the wait.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private Task DelayUntilAsync(long started, TimeSpan delay, CancellationToken cancellationToken)
    {
        var remaining = delay - time.GetElapsedTime(started);
        return remaining > TimeSpan.Zero
            ? Task.Delay(remaining, time, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task<HeartbeatOutcome> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        registrationAttempted = true;
        availability?.ProbeStarted();
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
                collisionWorld.Revision,
                itemCatalog.Revision,
                questCatalog.Revision),
            cancellationToken);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Simulation worker registry heartbeat failed for {WorkerId} runtime {RuntimeId}: {Code}.",
                config.SimulationWorkerId,
                identity.RuntimeId,
                result.Error!.Code);
            if (GameServiceOutcome.IsTransient(result.Error.Code, result.StatusCode))
            {
                availability?.HeartbeatFailed(result.Error.Code);
                return HeartbeatOutcome.Transient;
            }

            if (IsWaitingForPreviousRuntime(result.Error.Code))
            {
                return HeartbeatOutcome.Refused;
            }

            if (IsAuthorityFailure(result.Error.Code))
            {
                StopForLostAuthority(result.Error.Code);
            }

            return HeartbeatOutcome.Refused;
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
            return HeartbeatOutcome.Refused;
        }

        // The worker can only enforce a rule set its combat rules define.
        if (!combatRules.TryGetRuleSet(result.Value.RuleSet, out var ruleSet))
        {
            logger.LogError(
                "Simulation worker registry assigned Shard {ShardId} the rule set {RuleSet}, which the combat rules do not define.",
                result.Value.ShardId,
                result.Value.RuleSet);
            StopForLostAuthority("shard_rule_set_unknown");
            return HeartbeatOutcome.Refused;
        }

        shardRules.Set(ruleSet);
        if (renewalStart is not null)
        {
            registrationLease!.Renew(
                renewalStart.Value,
                result.Value.OnlineUntil - result.Value.LastHeartbeatAt);
        }

        hasHeldAuthority = true;
        acquireStartedTimestamp = null;
        availability?.HeartbeatSucceeded();
        return HeartbeatOutcome.Succeeded;
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

        acquireStartedTimestamp ??= time.GetTimestamp();
        var waited = time.GetElapsedTime(acquireStartedTimestamp.Value);
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
        stopReason?.Set(SimulationWorkerStopReason.ServerShutdownCode, LostAuthorityMessage);
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

    private enum HeartbeatOutcome
    {
        Succeeded,

        // Timed out, could not reach GameService, or got a server error: goes again soon.
        Transient,

        // Any other answer: goes again after the interval, unless it stopped the worker.
        Refused
    }
}
