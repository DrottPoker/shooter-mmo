using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Telemetry;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed class SimulationJoinService(
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    SimulationWorkerConfig config,
    SimulationWorkerIdentity identity,
    CharacterLookStore? lookStore = null,
    DevelopmentBotLooks? botLooks = null,
    CharacterQuestLogStore? questLogs = null,
    QuestCatalogRuntimeDocument? questCatalog = null,
    ILogger<SimulationJoinService>? logger = null,
    SimulationSessionReleaseService? releaseService = null)
{
    // One span covers the ticket consumption and any release a rejected join needs. The session
    // store counts the join as in flight until it registered its session or failed, because a
    // reconnect's consumption rotates the session's token at GameService before that.
    public async Task<SimulationJoinResult<ActiveSimulationSession>> JoinAsync(
        RealtimeJoinRequest joinRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(joinRequest);
        using var activity = SimulationWorkerTelemetry.StartOperation("simulation.join");
        var join = sessionStore.BeginJoin();
        SimulationJoinResult<ActiveSimulationSession> result;
        try
        {
            result = await JoinCoreAsync(joinRequest, cancellationToken);
        }
        finally
        {
            sessionStore.EndJoin(join);
        }

        if (!result.Succeeded)
        {
            SimulationWorkerTelemetry.RecordFailure(activity, result.Error!.Code);
        }

        return result;
    }

    private async Task<SimulationJoinResult<ActiveSimulationSession>> JoinCoreAsync(
        RealtimeJoinRequest joinRequest,
        CancellationToken cancellationToken)
    {
        if (joinRequest.TicketId == Guid.Empty)
        {
            return SimulationJoinResult<ActiveSimulationSession>.BadRequest(
                "missing_join_ticket",
                "Join ticket is required.");
        }

        var requestedAt = DateTime.UtcNow;
        var consumedTicket = await gameServiceClient.ConsumeJoinTicketAsync(
            joinRequest,
            config.SimulationWorkerId,
            identity.RuntimeId,
            config.ShardId,
            cancellationToken);

        if (!consumedTicket.Succeeded)
        {
            return SimulationJoinResult<ActiveSimulationSession>.Failure(
                consumedTicket.StatusCode,
                consumedTicket.Error!.Code,
                consumedTicket.Error.Message);
        }

        // The worker keeps only the keys of the datagrams; the transport key and the proof key go.
        var transportKeys = RealtimeTransportKeys.Derive(consumedTicket.Value!.TransportKey);
        Array.Clear(consumedTicket.Value.TransportKey);
        Array.Clear(transportKeys.ProofKey);
        SimulationJoinResult<ActiveSimulationSession> result;
        try
        {
            result = await RegisterConsumedTicketAsync(consumedTicket.Value, requestedAt, cancellationToken);
        }
        catch (Exception)
        {
            transportKeys.Dispose();
            throw;
        }

        if (!result.Succeeded)
        {
            transportKeys.Dispose();
            return result;
        }

        return result with { TransportKeys = transportKeys };
    }

    private async Task<SimulationJoinResult<ActiveSimulationSession>> RegisterConsumedTicketAsync(
        ConsumedSimulationJoinTicketResponse consumedTicket,
        DateTime requestedAt,
        CancellationToken cancellationToken)
    {

        if (!string.Equals(consumedTicket.WorkerId, config.SimulationWorkerId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.WorkerRuntimeId, identity.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.ShardId, config.ShardId, StringComparison.Ordinal)
            || !string.Equals(consumedTicket.WorldId, config.WorldId, StringComparison.Ordinal))
        {
            await gameServiceClient.ReleaseSimulationSessionAsync(
                consumedTicket.SimulationSessionId,
                identity.RuntimeId,
                consumedTicket.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "wrong_simulation_assignment",
                "Join ticket does not match this simulation worker assignment.");
        }

        // GameService does not know the worker's outage tolerance, so the worker checks that a
        // session it grants outlasts a whole tolerated outage and the round that renews it after.
        if (consumedTicket.SessionLeaseDurationMilliseconds < config.MinimumSessionLease.TotalMilliseconds)
        {
            logger?.LogError(
                "Refused the join of simulation session {SimulationSessionId}: GameService granted a session lease of {LeaseMilliseconds} ms, shorter than SimulationWorker:GameServiceOutageToleranceSeconds plus SimulationWorker:SimulationSessionHeartbeatSeconds ({MinimumMilliseconds} ms). Raise Simulation:SessionLeaseLifetimeSeconds in GameService or lower the tolerance.",
                consumedTicket.SimulationSessionId,
                consumedTicket.SessionLeaseDurationMilliseconds,
                (long)config.MinimumSessionLease.TotalMilliseconds);
            await gameServiceClient.ReleaseSimulationSessionAsync(
                consumedTicket.SimulationSessionId,
                identity.RuntimeId,
                consumedTicket.SimulationSessionToken,
                cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Failure(
                500,
                "join_failed",
                "The session lease is shorter than this worker's GameService outage tolerance.");
        }

        var session = ActiveSimulationSession.FromJoinTicket(consumedTicket, requestedAt);
        try
        {
            return await RegisterAsync(session, consumedTicket, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // GameService gave this join the session, so a join that fails unexpectedly after the
            // consumption releases it as a session that no peer owns: neither the session nor the
            // state that a body of it left stays behind, and GameService keeps that state.
            await ReleaseAfterFailureAsync(session);
            throw;
        }
    }

    // Reads the quest log of the session that the ticket gave, and registers the session with its
    // item state, look, and quest log.
    private async Task<SimulationJoinResult<ActiveSimulationSession>> RegisterAsync(
        ActiveSimulationSession session,
        ConsumedSimulationJoinTicketResponse consumedTicket,
        CancellationToken cancellationToken)
    {
        var questLog = await ReadQuestLogAsync(session, cancellationToken);

        // A reconnect of a session that the worker hosts keeps the quest log it holds when the read
        // fails for a moment, and reads it again before the next quest action, because releasing
        // the session would lose the death or the kill credits it may still have to commit.
        var keepsQuestLog = !questLog.Succeeded
            && GameServiceOutcome.IsTransient(questLog.Error!.Code, questLog.StatusCode)
            && HoldsQuestLogOf(session);
        if (!questLog.Succeeded && !keepsQuestLog)
        {
            await ReleaseAsync(session, cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Failure(
                questLog.StatusCode,
                questLog.Error!.Code,
                questLog.Error.Message);
        }

        var registration = sessionStore.Register(session);

        if (registration == ActiveSimulationSessionRegistration.Conflict)
        {
            await ReleaseAsync(session, cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "character_already_active",
                "Character already has a different active simulation session.");
        }

        var carryRegistration = itemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterItemState.Create(session.CarryState, consumedTicket.Equipment),
            out var currentItemState);
        if (carryRegistration == ItemStateApplyResult.Conflict)
        {
            var invalidated = sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                "carry_state_conflict",
                "GameService returned conflicting carry state for one item-state revision.");
            if (invalidated)
            {
                itemStateStore.Remove(session.CharacterId, session.SimulationSessionId);
            }

            await ReleaseAsync(session, cancellationToken);

            return SimulationJoinResult<ActiveSimulationSession>.Conflict(
                "carry_state_conflict",
                "The authoritative carry state is inconsistent.");
        }

        sessionStore.RefreshCarryState(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            currentItemState.Carry);

        // A development bot has no stored look, so the worker dresses it from its id.
        var look = consumedTicket.Appearance is { } appearance
            ? new CharacterLook(appearance)
            : botLooks?.For(session.CharacterId);
        if (look is not null)
        {
            lookStore?.Register(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken, look);
        }

        if (keepsQuestLog)
        {
            questLogs!.MarkStale(session.CharacterId, session.SimulationSessionId);
        }
        else if (questLog.Value is not null)
        {
            questLogs?.Register(session.CharacterId, session.SimulationSessionId, questLog.Value);
        }

        sessionStore.TryGet(session.CharacterId, out var registeredSession);

        // The caller adds the transport keys.
        return new SimulationJoinResult<ActiveSimulationSession>(registeredSession!, null, 200);
    }

    // A join that cannot go on releases the session it consumed. A body of the session that went
    // before this join rotated the session's token left a final state, whose own release
    // GameService refused; this release carries it, so GameService keeps the state the body left
    // instead of an older one.
    private async Task ReleaseAsync(ActiveSimulationSession session, CancellationToken cancellationToken)
    {
        if (releaseService is not null)
        {
            await releaseService.ReleaseAsync(session, cancellationToken);
            return;
        }

        await gameServiceClient.ReleaseSimulationSessionAsync(
            session.SimulationSessionId,
            session.WorkerRuntimeId,
            session.SimulationSessionToken,
            cancellationToken);
    }

    // Releases the session of a join that failed unexpectedly, with the local state that the join
    // may have registered for it; a release that fails too only logs, so the join's own failure is
    // what the caller sees.
    private async Task ReleaseAfterFailureAsync(ActiveSimulationSession session)
    {
        try
        {
            if (releaseService is not null)
            {
                await releaseService.ReleaseDetachedAsync(session, CancellationToken.None);
                return;
            }

            await gameServiceClient.ReleaseSimulationSessionAsync(
                session.SimulationSessionId,
                session.WorkerRuntimeId,
                session.SimulationSessionToken,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Could not release simulation session {SimulationSessionId} after its join failed.",
                session.SimulationSessionId);
        }
    }

    private bool HoldsQuestLogOf(ActiveSimulationSession session)
    {
        return questLogs is not null
            && sessionStore.TryGet(session.CharacterId, out var current)
            && current!.SimulationSessionId == session.SimulationSessionId
            && questLogs.TryGet(session.CharacterId, session.SimulationSessionId, out _);
    }

    // The worker keeps the quest log of every player while it plays, so it counts quest progress
    // without asking GameService. Development bots never take part in quests. GameService may
    // have started with another quest catalog since it placed the character here.
    private async Task<GameServiceResult<CharacterQuestLog?>> ReadQuestLogAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        if (questLogs is null || session.IsSyntheticBot)
        {
            return GameServiceResult<CharacterQuestLog?>.Success(null);
        }

        var result = await gameServiceClient.ReadSimulationQuestLogAsync(
            session.SimulationSessionId,
            new SimulationQuestLogRequest(
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                questCatalog?.Revision ?? string.Empty),
            cancellationToken);
        if (!result.Succeeded)
        {
            return GameServiceResult<CharacterQuestLog?>.Failure(
                result.StatusCode,
                result.Error!.Code,
                result.Error.Message);
        }

        if (questCatalog is not null
            && !string.Equals(result.Value!.QuestCatalogRevision, questCatalog.Revision, StringComparison.Ordinal))
        {
            return GameServiceResult<CharacterQuestLog?>.Failure(
                409,
                "simulation_worker_quest_catalog_mismatch",
                "The simulation worker runs another quest catalog revision than GameService.");
        }

        return GameServiceResult<CharacterQuestLog?>.Success(CharacterQuestLog.From(result.Value!));
    }
}
