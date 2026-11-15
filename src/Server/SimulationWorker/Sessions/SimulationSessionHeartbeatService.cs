using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Telemetry;

namespace Mmorpg.SimulationWorker.Sessions;

// Renews the lease of every active session once per interval, with one GameService request per
// batch of sessions, and releases every session when the worker stops. While GameService does not
// answer, no round goes, because the session leases outlast the outage that the worker tolerates;
// one round goes as soon as GameService answers again.
public sealed class SimulationSessionHeartbeatService(
    GameServiceClient gameServiceClient,
    SimulationSessionReleaseService releaseService,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    SimulationWorkerConfig config,
    ILogger<SimulationSessionHeartbeatService> logger,
    GameServiceAvailability? availability = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.SimulationSessionHeartbeatInterval, time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (availability is not null
                && !availability.IsAvailable
                && !await availability.WaitUntilAvailableAsync(stoppingToken))
            {
                return;
            }

            await HeartbeatRoundAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // A release that cannot reach GameService would only spend its timeout; the leases
        // expire on their own, and a runtime that replaces this one releases them at once.
        if (availability is { IsAvailable: false })
        {
            logger.LogWarning(
                "GameService does not answer, so the {Count} simulation sessions are not released at shutdown; their leases expire.",
                sessionStore.ListActiveSessions().Count);
            await base.StopAsync(cancellationToken);
            return;
        }

        await BoundedAsync.ForEachAsync(
            sessionStore.ListActiveSessions(),
            config.SimulationSessionHeartbeatMaxConcurrency,
            async (session, token) =>
            {
                try
                {
                    var result = await releaseService.ReleaseAsync(session, token);

                    if (result.Succeeded)
                    {
                        return;
                    }

                    logger.LogWarning(
                        "Could not release simulation session {SimulationSessionId} during shutdown: {Code}.",
                        session.SimulationSessionId,
                        result.Code);
                }
                catch (Exception exception) when (IsExpectedTransportException(exception))
                {
                    logger.LogWarning(
                        exception,
                        "Could not release simulation session {SimulationSessionId} during shutdown.",
                        session.SimulationSessionId);
                }
            },
            cancellationToken);

        await base.StopAsync(cancellationToken);
    }

    // One heartbeat round: every active session, in requests of at most the batch size, several
    // at once. A request that failed for a passing reason, such as a timeout or a GameService that
    // cannot be reached, goes again after the retry delay with those of its sessions that are
    // still current, as long as the retry starts within the round's interval. So a failed request
    // costs no lease when GameService comes back in time, and a session whose lease could not be
    // renewed still ends by its local deadline.
    internal async Task HeartbeatRoundAsync(CancellationToken cancellationToken)
    {
        var sessions = sessionStore.ListActiveSessions();
        if (sessions.Count == 0)
        {
            return;
        }

        // One trace per round instead of one per session.
        using var activity = SimulationWorkerTelemetry.StartOperation(
            "simulation.session.heartbeat_cycle");
        activity?.SetTag("mmorpg.simulation_session.count", sessions.Count);
        var roundStarted = time.GetTimestamp();
        IReadOnlyList<ActiveSimulationSession[]> batches = sessions
            .GroupBy(session => session.WorkerRuntimeId, StringComparer.Ordinal)
            .SelectMany(runtime => runtime.Chunk(config.SimulationSessionHeartbeatBatchSize))
            .ToArray();
        while (true)
        {
            var failed = new ConcurrentQueue<ActiveSimulationSession[]>();
            await BoundedAsync.ForEachAsync(
                batches,
                config.SimulationSessionHeartbeatMaxConcurrency,
                async (batch, token) =>
                {
                    if (await HeartbeatBatchAsync(batch, token))
                    {
                        failed.Enqueue(batch);
                    }
                },
                cancellationToken);
            if (failed.IsEmpty)
            {
                return;
            }

            if (time.GetElapsedTime(roundStarted) + config.SimulationSessionHeartbeatRetryDelay
                    >= config.SimulationSessionHeartbeatInterval
                || availability is { IsAvailable: false })
            {
                logger.LogWarning(
                    "Could not renew the leases of {Count} simulation sessions in this heartbeat round; the next round sends them again.",
                    failed.Sum(batch => batch.Length));
                return;
            }

            await Task.Delay(config.SimulationSessionHeartbeatRetryDelay, time, cancellationToken);
            if (availability is { IsAvailable: false })
            {
                return;
            }

            batches = failed
                .Select(batch => batch.Where(sessionStore.IsCurrentGeneration).ToArray())
                .Where(batch => batch.Length > 0)
                .ToArray();
            if (batches.Count == 0)
            {
                return;
            }
        }
    }

    // Sends the heartbeat of one batch and applies the answer of every session. True when the
    // request failed for a reason that the same request may get past, so the round sends it again.
    private async Task<bool> HeartbeatBatchAsync(
        ActiveSimulationSession[] batch,
        CancellationToken cancellationToken)
    {
        var request = new SimulationSessionHeartbeatRequest(
            batch[0].WorkerRuntimeId,
            batch
                .Select(session => new SimulationSessionHeartbeatEntry(
                    session.SimulationSessionId,
                    session.SimulationSessionToken))
                .ToArray());
        GameServiceResult<SimulationSessionHeartbeatResponse> result;
        var requestedAt = time.GetUtcNow().UtcDateTime;
        try
        {
            result = await gameServiceClient.HeartbeatSimulationSessionsAsync(
                config.SimulationWorkerId,
                request,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedTransportException(exception))
        {
            logger.LogWarning(
                exception,
                "Could not heartbeat {Count} simulation sessions.",
                batch.Length);
            return exception is HttpRequestException or TaskCanceledException;
        }

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Could not heartbeat {Count} simulation sessions: {Code}.",
                batch.Length,
                result.Error!.Code);
            return GameServiceOutcome.IsTransient(result.Error.Code, result.StatusCode);
        }

        for (var index = 0; index < batch.Length; index++)
        {
            Apply(batch[index], result.Value!.Sessions[index], requestedAt);
        }

        return false;
    }

    // Applies GameService's answer for one session, as the heartbeat of a single session did.
    private void Apply(
        ActiveSimulationSession session,
        SimulationSessionHeartbeatResult result,
        DateTime requestedAt)
    {
        if (result.Lease is not { } lease)
        {
            // The lease was not renewed, so the session ended whatever the code says.
            var refusal = GameServiceClient.NormalizeForwardedErrorCode(result.Error!.Code);
            var (code, message) = GameServiceOutcome.AccountSessionEndDisconnect(refusal);
            var invalidated = sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                code,
                message);
            if (invalidated)
            {
                itemStateStore.Remove(
                    session.CharacterId,
                    session.SimulationSessionId);
            }

            logger.LogWarning(
                "Invalidated simulation session {SimulationSessionId} with code {Code} after a refused heartbeat: {Refusal}.",
                session.SimulationSessionId,
                code,
                refusal);
            return;
        }

        var refreshed = sessionStore.Refresh(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            requestedAt.AddMilliseconds(lease.LeaseDurationMilliseconds));
        if (!refreshed)
        {
            return;
        }

        // The account session ended, but the session goes on: the realtime loop ends the
        // connection that still plays it, and the character's body stays its full time.
        if (lease.AccountSessionEnded is { } accountSessionEnd
            && sessionStore.MarkAccountSessionEnded(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                accountSessionEnd))
        {
            logger.LogInformation(
                "The account session of simulation session {SimulationSessionId} ended with {Code}; its connection ends and its body stays.",
                session.SimulationSessionId,
                accountSessionEnd);
        }

        var itemState = CharacterItemState.Create(
            new PlayerCarryState(
                lease.ItemStateRevision,
                lease.CarriedWeight,
                lease.CarryCapacity),
            lease.Equipment);
        var carryResult = carryApplier.Apply(session, itemState, out _);
        if (carryResult == ItemStateApplyResult.Conflict)
        {
            var invalidated = sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                "carry_state_conflict",
                "GameService returned conflicting carry state for one item-state revision.");
            if (invalidated)
            {
                itemStateStore.Remove(
                    session.CharacterId,
                    session.SimulationSessionId);
            }

            logger.LogError(
                "Invalidated simulation session {SimulationSessionId} because GameService returned conflicting carry state revision {ItemStateRevision}.",
                session.SimulationSessionId,
                itemState.ItemStateRevision);
        }
    }

    private static bool IsExpectedTransportException(Exception exception)
    {
        return exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or NotSupportedException;
    }
}
