using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Contracts;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Health;
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
//
// A worker whose realtime loop has been silent for longer than the health file's LoopSilenceLimit
// sends no renewal, so the registry lease ends and GameService takes the Shard offline by the rules
// that exist, instead of placing players on a worker that simulates nothing. Durable actions pause
// meanwhile, since a lease that is stale at GameService would end the sessions of the players who
// ask. When the loop ticks again the next heartbeat renews the same runtime, which GameService
// accepts however long it was gone, as long as no other runtime replaced it. The worker never kills
// itself for a hung loop; whoever restarts unhealthy workers does, as docs/DEPLOYMENT.md describes.
//
// The first answer is also the one place where the worker can check the timing of GameService
// against its own, as docs/SERVICE_FEATURES.md describes under GameService Outage.
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
    TimeProvider? timeProvider = null,
    RealtimeLoopLiveness? loopLiveness = null,
    GameServiceCallMetrics? callMetrics = null) : BackgroundService
{
    private const string WorkerRuntimeChangedCode = "worker_runtime_changed";
    private const string LostAuthorityMessage = "The shard lost its authority and is shutting down.";

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private volatile bool registrationAttempted;
    private volatile bool hasHeldAuthority;
    private long? acquireStartedTimestamp;
    private long listeningSinceTimestamp;
    private bool withholding;
    private bool timingsChecked;
    private bool timingsRejected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await transportReadiness.WaitUntilListeningAsync(stoppingToken);
        listeningSinceTimestamp = time.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            var outcome = LoopIsSilent(out var silence)
                ? WithholdRenewal(silence)
                : await SendHeartbeatAsync(stoppingToken);
            if (timingsRejected)
            {
                // The worker is stopping for its timing; another heartbeat would only repeat the message.
                return;
            }

            await WaitForNextHeartbeatAsync(
                started,
                outcome is HeartbeatOutcome.Transient or HeartbeatOutcome.Withheld
                    ? config.RegistryHeartbeatRetryDelay
                    : config.RegistryHeartbeatInterval,
                stoppingToken);
        }
    }

    // The loop has completed no tick for longer than a ready worker allows. A loop that never ticked
    // is counted from the moment the transport listened, so a worker whose loop never starts does
    // not register either, while one that is only starting is not held back.
    private bool LoopIsSilent(out TimeSpan silence)
    {
        silence = TimeSpan.Zero;
        if (loopLiveness is null)
        {
            return false;
        }

        silence = loopLiveness.SilenceFor(time) ?? time.GetElapsedTime(listeningSinceTimestamp);
        return silence > SimulationWorkerHealthFileService.LoopSilenceLimit;
    }

    // Sends nothing, so the registry lease ends, and says so once per episode.
    private HeartbeatOutcome WithholdRenewal(TimeSpan silence)
    {
        availability?.RegistrationWithheld();
        if (!withholding)
        {
            withholding = true;
            callMetrics?.RecordRenewalsWithheld();
            logger.LogError(
                "[SIMULATION] The realtime loop of worker {WorkerId} runtime {RuntimeId} completed no tick for {SilentSeconds:0.0} s, more than the {LimitSeconds:0} s that a ready worker allows, so the worker stops renewing its registry lease: GameService takes the Shard offline when the lease ends, and the worker registers again when the loop ticks. A worker that stays silent needs a restart.",
                config.SimulationWorkerId,
                identity.RuntimeId,
                silence.TotalSeconds,
                SimulationWorkerHealthFileService.LoopSilenceLimit.TotalSeconds);
        }

        return HeartbeatOutcome.Withheld;
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
        if (withholding)
        {
            withholding = false;
            logger.LogWarning(
                "[SIMULATION] The realtime loop of worker {WorkerId} runtime {RuntimeId} ticks again, so the worker registers again.",
                config.SimulationWorkerId,
                identity.RuntimeId);
        }

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
                questCatalog.Revision,
                ServiceContract.Version),
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

            if (string.Equals(result.Error.Code, ServiceContract.MismatchCode, StringComparison.Ordinal))
            {
                LogContractMismatch(result.Error.Message);
                return HeartbeatOutcome.Refused;
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

        if (!timingsChecked)
        {
            if (!TimingsAreCompatible(result.Value))
            {
                return HeartbeatOutcome.Refused;
            }

            timingsChecked = true;
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

    // GameService and the worker run different versions of the call between them. Nothing here is a
    // credential failure, so the worker neither stops nor treats it as lost authority: it plays
    // nobody, since without a renewed registration no join is accepted, and tries again at its normal
    // pace, so that updating the older side is all an operator has to do.
    private void LogContractMismatch(string gameServiceMessage)
    {
        var gameServiceVersion = ServiceContract.TryReadGameServiceVersion(gameServiceMessage);
        var older = gameServiceVersion is not { } version
            ? "The versions in GameService's message tell which side is older."
            : version > ServiceContract.Version
                ? "This worker is the older side: start the worker of the version that GameService runs."
                : version < ServiceContract.Version
                    ? "GameService is the older side: update it, with migrate, as docs/DEPLOYMENT.md describes, and the worker then registers."
                    : "Both sides report the same version, so GameService's answer is not to be trusted; check the deployment.";
        logger.LogError(
            "GameService refused the registry heartbeat of worker {WorkerId} runtime {RuntimeId} with {Code}: this worker runs service contract version {WorkerVersion}. {GameServiceMessage} {Older} The worker plays nobody and tries again every {Interval}.",
            config.SimulationWorkerId,
            identity.RuntimeId,
            ServiceContract.MismatchCode,
            ServiceContract.Version,
            gameServiceMessage,
            older,
            config.RegistryHeartbeatInterval);
    }

    // The first answer shows what GameService grants, which the worker cannot know before: the
    // registry lease must outlast a heartbeat and its answer, and the session lease the longest
    // outage that this worker tolerates and the round that renews it afterwards. Stops the worker
    // with one message that names the settings of both sides when either fails.
    private bool TimingsAreCompatible(SimulationWorkerHeartbeatResponse response)
    {
        var problems = new List<string>();
        var registryLease = response.OnlineUntil - response.LastHeartbeatAt;
        if (registryLease < config.MinimumRegistryLease)
        {
            problems.Add(
                $"GameService grants a registry lease of {registryLease.TotalSeconds:0.#} s (Simulation:WorkerHeartbeatTimeoutSeconds), but this worker needs more than {config.MinimumRegistryLease.TotalSeconds:0.#} s, its SimulationWorker:RegistryHeartbeatSeconds plus SimulationWorker:GameServiceTimeoutSeconds plus 1 s, so that the lease never ends between two heartbeats");
        }

        var sessionLease = TimeSpan.FromSeconds(response.SessionLeaseSeconds);
        if (sessionLease < config.MinimumSessionLease)
        {
            problems.Add(
                $"GameService grants a session lease of {sessionLease.TotalSeconds:0.#} s (Simulation:SessionLeaseLifetimeSeconds), but this worker needs at least {config.MinimumSessionLease.TotalSeconds:0.#} s, the sum of its SimulationWorker:GameServiceOutageToleranceSeconds, SimulationWorker:RegistryHeartbeatRetryMilliseconds, SimulationWorker:GameServiceTimeoutSeconds, SimulationWorker:SimulationSessionHeartbeatSeconds, and SimulationWorker:SimulationSessionHeartbeatRetryMilliseconds plus {SimulationWorkerConfig.OutageMonitorPollInterval.TotalSeconds:0} s, so that GameService still holds every session when the worker comes back within its tolerance");
        }

        if (problems.Count == 0)
        {
            return true;
        }

        timingsRejected = true;
        registrationLease?.Clear();
        logger.LogCritical(
            "The timing settings of worker {WorkerId} and of GameService cannot work together, so the worker stops before it plays anyone: {Problems}. Change the settings on either side and start the worker again.",
            config.SimulationWorkerId,
            string.Join("; and ", problems));
        applicationLifetime?.StopApplication();
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
        Refused,

        // Nothing was sent because the realtime loop is silent: looks again soon.
        Withheld
    }
}
