using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Identity;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// Commits a character's spent arrows and its death through the character's authority
// lane, so they never race its item and corpse operations. A batch starts 5 s after the
// first uncommitted shot, before any other client operation, and before the session
// ends; a death carries the shots that are still uncommitted. Both retry with backoff
// after an uncertain answer and outlive a disconnect, which releases the session only
// after they committed. Loop-confined; GameService calls run in
// PlayerCombatAuthorityOperations.
internal sealed class PlayerCombatAuthority(
    SimulationWorkerConfig config,
    CombatRules rules,
    RealtimeSimulationClock clock,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    CharacterItemStateStore itemStateStore,
    ActiveSimulationSessionStore sessionStore,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    DurableCorpseStore corpseStore,
    AuthorityOperationScheduler scheduler,
    RealtimeSessionTeardown teardown,
    SessionAuthorityOperations sessionOperations,
    RealtimeOperationTracker operationTracker,
    PlayerCombatAuthorityOperations operations,
    CombatMetrics? metrics,
    ILogger logger) : IRealtimeCompletionHandler, ICombatOperationGate
{
    public const string PlayerCorpsePresentationKey = "corpse.remains";

    // An uncertain batch gives up after this many attempts; its arrows stay with the player.
    private const int MaximumBatchAttempts = 8;

    private readonly CommittedCarryApplier applier = new(itemStateStore, sessionStore);
    private readonly Dictionary<Guid, PlayerCombatSession> sessions = [];
    private readonly List<PlayerCombatSession> sessionBuffer = [];

    // Starts a leave that waited for durable combat work, with the handler that finalizes it.
    public Action<RealtimePeerContext, ActiveSimulationSession>? StartLeave { get; set; }

    // Tells the combat system that a death committed or a batch changed the arrow count.
    public Action<PlayerCombatSession>? OwnerStateChanged { get; set; }

    public int SessionCount => sessions.Count;

    public PlayerCombatSession Attach(RealtimePeerContext context, ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var existing))
        {
            existing.Context = context;
            existing.Session = session;
            existing.Detached = false;
            existing.LeaveRequested = false;
            return existing;
        }

        var combatSession = new PlayerCombatSession(session, context);
        sessions.Add(session.SimulationSessionId, combatSession);
        return combatSession;
    }

    public bool TryGet(Guid simulationSessionId, out PlayerCombatSession combatSession)
    {
        return sessions.TryGetValue(simulationSessionId, out combatSession!);
    }

    public bool IsCharacterDead(RealtimePeerContext context)
    {
        return TryGetAttached(context, out var combatSession) && combatSession.Death is not null;
    }

    public void BeforeClientOperation(RealtimePeerContext context)
    {
        if (TryGetAttached(context, out var combatSession))
        {
            EnqueueBatch(combatSession);
        }
    }

    public void ClientOperationStarting(RealtimePeerContext context, bool mayChangeEquipment)
    {
        if (TryGetAttached(context, out var combatSession))
        {
            combatSession.EquipmentChangePending = mayChangeEquipment;
        }
    }

    public void ClientOperationFinished(RealtimePeerContext context)
    {
        if (TryGetAttached(context, out var combatSession))
        {
            combatSession.EquipmentChangePending = false;
        }
    }

    public long RebaseExpectedCharacterRevision(RealtimePeerContext context, long expectedCharacterRevision)
    {
        if (!TryGetAttached(context, out var combatSession)
            || combatSession.BatchRevisionChain.Count < 2
            || !itemStateStore.TryGetItemState(
                combatSession.Session.CharacterId,
                combatSession.Session.SimulationSessionId,
                out var state))
        {
            return expectedCharacterRevision;
        }

        var chain = combatSession.BatchRevisionChain;
        return chain[^1] == state!.ItemStateRevision && chain.Contains(expectedCharacterRevision)
            ? state.ItemStateRevision
            : expectedCharacterRevision;
    }

    // The committed stack minus every shot that GameService has not committed yet.
    public (Guid ItemInstanceId, int Quantity) GetLiveAmmunition(PlayerCombatSession combatSession, EquippedAmmunition? committed)
    {
        if (committed is null)
        {
            return (Guid.Empty, 0);
        }

        var inFlight = combatSession.InFlightBatch is { } batch
            && batch.ItemInstanceId == committed.ItemInstanceId
            && batch.ExpectedItemRevision == committed.Revision
                ? batch.Quantity
                : 0;
        return (committed.ItemInstanceId, Math.Max(0, committed.Quantity - inFlight - combatSession.PendingShots));
    }

    public bool TrySpendArrow(PlayerCombatSession combatSession, EquippedAmmunition? committed)
    {
        if (GetLiveAmmunition(combatSession, committed).Quantity < 1)
        {
            return false;
        }

        if (combatSession.PendingShots == 0)
        {
            combatSession.FirstPendingShotTick = clock.ServerTick;
        }

        combatSession.PendingShots++;
        return true;
    }

    // A character died: every waiting client operation is rejected, and the death runs
    // next in its lane with the shots that are still uncommitted.
    public void BeginDeath(
        PlayerCombatSession combatSession,
        SimulationDeathKillerRequest? killer,
        PlayerMovementState position)
    {
        if (combatSession.Death is not null)
        {
            return;
        }

        combatSession.Death = new PlayerDeathAttempt(
            Guid.NewGuid(),
            killer,
            position.PositionX,
            position.PositionY,
            position.PositionZ,
            position.YawDegrees);
        var context = combatSession.Context;
        while (context.AuthorityOperationQueue.TryDequeue(out var waiting))
        {
            if (waiting is QueuedAmmunitionBatch)
            {
                combatSession.BatchQueued = false;
                continue;
            }

            waiting.Cancel(context, RealtimeCombatErrorCodes.CharacterDead, "The character is dead.");
        }

        QueueDeath(combatSession);
    }

    public void ClearDeath(PlayerCombatSession combatSession)
    {
        combatSession.Death = null;
    }

    // Runs every tick: due batches and retries, and detached sessions that still commit.
    public void ProcessTick()
    {
        if (sessions.Count == 0)
        {
            return;
        }

        sessionBuffer.Clear();
        sessionBuffer.AddRange(sessions.Values);
        var serverTick = clock.ServerTick;
        var batchIntervalTicks = CombatTicks.FromSeconds(
            rules.AmmunitionBatchIntervalSeconds,
            config.MovementSimulation.TickRateHz);
        foreach (var combatSession in sessionBuffer)
        {
            if (combatSession.InFlightBatch is { RetryAtTick: { } batchRetry }
                && CombatTicks.IsReached(serverTick, batchRetry))
            {
                combatSession.InFlightBatch.RetryAtTick = null;
                SendBatch(combatSession, combatSession.InFlightBatch);
            }

            if (combatSession.Death is { RetryAtTick: { } deathRetry } death
                && CombatTicks.IsReached(serverTick, deathRetry))
            {
                death.RetryAtTick = null;
                SendDeath(combatSession, death);
            }

            if (combatSession.Detached)
            {
                DriveDetached(combatSession);
                continue;
            }

            if (combatSession.PendingShots > 0
                && combatSession.Death is null
                && CombatTicks.IsReached(serverTick, combatSession.FirstPendingShotTick + batchIntervalTicks))
            {
                EnqueueBatch(combatSession);
            }
        }

        metrics?.SetPendingShots(sessions.Values.Sum(value => value.PendingShots));
    }

    // The peer disconnected. Work that has not committed keeps the session; it is
    // released once the work is done.
    public bool TryDeferRelease(RealtimePeerContext context, ActiveSimulationSession session)
    {
        if (!sessions.TryGetValue(session.SimulationSessionId, out var combatSession)
            || !ReferenceEquals(combatSession.Context, context))
        {
            return false;
        }

        if (!combatSession.HasDurableWork)
        {
            sessions.Remove(session.SimulationSessionId);
            return false;
        }

        combatSession.Detached = true;
        combatSession.BatchQueued = false;
        itemStateStore.TryGetItemState(session.CharacterId, session.SimulationSessionId, out var itemState);
        combatSession.DetachedItemState = itemState;
        if (combatSession.Death is { InFlight: false, RetryAtTick: null } death)
        {
            death.Queued = false;
        }

        return true;
    }

    // A leave waits for every durable combat operation, which runs before it in the lane.
    public bool TryDeferLeave(RealtimePeerContext context, ActiveSimulationSession session)
    {
        if (!TryGetAttached(context, out var combatSession) || !combatSession.HasDurableWork)
        {
            return false;
        }

        combatSession.LeaveRequested = true;
        if (combatSession.Death is null)
        {
            EnqueueBatch(combatSession);
        }

        scheduler.Enqueue(context, new QueuedLeave(this, combatSession));
        return true;
    }

    public void OnSessionEnded(ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var combatSession)
            && !combatSession.Detached)
        {
            sessions.Remove(session.SimulationSessionId);
        }
    }

    public void Complete(RealtimeOperationResult completion)
    {
        switch (completion)
        {
            case AmmunitionBatchCompleted batch:
                CompleteBatch(batch);
                break;
            case PlayerDeathCompleted death:
                CompleteDeath(death);
                break;
        }
    }

    // A worker that stops commits the uncommitted shots it can still name exactly. A batch
    // whose answer arrived but was never finalized is settled first, so the shots behind a
    // committed batch commit on top of its state. A batch without a committed answer is
    // left alone, and its arrows stay with the player.
    public async Task FlushOnShutdownAsync(IReadOnlyList<RealtimeOperationResult> unfinalized)
    {
        ArgumentNullException.ThrowIfNull(unfinalized);
        var committedStates = new Dictionary<PlayerCombatSession, CharacterItemState>();
        foreach (var completed in unfinalized.OfType<AmmunitionBatchCompleted>())
        {
            var combatSession = completed.CombatSession;
            if (ReferenceEquals(combatSession.InFlightBatch, completed.Batch)
                && completed.Result.Succeeded
                && completed.Result.Value!.CharacterState is { } state)
            {
                combatSession.InFlightBatch = null;
                committedStates[combatSession] = CharacterItemState.From(state);
            }
        }

        var flushes = new List<Task>();
        foreach (var combatSession in sessions.Values)
        {
            if (combatSession.PendingShots <= 0
                || combatSession.InFlightBatch is not null
                || combatSession.Death is not null
                || !TryCreateBatch(
                    combatSession,
                    committedStates.GetValueOrDefault(combatSession),
                    out var batch))
            {
                continue;
            }

            combatSession.InFlightBatch = batch;
            combatSession.PendingShots -= batch!.Quantity;
            flushes.Add(operations.ConsumeAmmunitionAsync(
                this,
                combatSession.Context,
                combatSession,
                batch,
                CreateBatchRequest(combatSession, batch)));
        }

        await Task.WhenAll(flushes);
    }

    public void Clear()
    {
        sessions.Clear();
    }

    private bool TryGetAttached(RealtimePeerContext context, out PlayerCombatSession combatSession)
    {
        combatSession = null!;
        return context.Session is not null
            && sessions.TryGetValue(context.Session.SimulationSessionId, out combatSession!)
            && ReferenceEquals(combatSession.Context, context)
            && !combatSession.Detached;
    }

    private void EnqueueBatch(PlayerCombatSession combatSession)
    {
        if (combatSession.PendingShots <= 0
            || combatSession.BatchQueued
            || combatSession.InFlightBatch is not null
            || combatSession.Death is not null)
        {
            return;
        }

        combatSession.BatchQueued = true;
        scheduler.Enqueue(combatSession.Context, new QueuedAmmunitionBatch(this, combatSession));
    }

    private void QueueDeath(PlayerCombatSession combatSession)
    {
        combatSession.Death!.Queued = true;
        scheduler.Enqueue(combatSession.Context, new QueuedPlayerDeath(this, combatSession));
    }

    private void StartBatch(RealtimePeerContext context, PlayerCombatSession combatSession)
    {
        combatSession.BatchQueued = false;
        if (combatSession.Death is not null
            || combatSession.InFlightBatch is not null
            || combatSession.PendingShots <= 0)
        {
            ReleaseLane(combatSession);
            return;
        }

        if (!TryCreateBatch(combatSession, null, out var batch))
        {
            DropPendingShots(combatSession, "ammunition_missing");
            ReleaseLane(combatSession);
            return;
        }

        combatSession.InFlightBatch = batch;
        combatSession.PendingShots -= batch!.Quantity;
        SendBatch(combatSession, batch);
    }

    // A detached session's state left the store with its entity, so it keeps its own copy.
    private CharacterItemState? ResolveItemState(PlayerCombatSession combatSession)
    {
        return itemStateStore.TryGetItemState(
                combatSession.Session.CharacterId,
                combatSession.Session.SimulationSessionId,
                out var state)
            ? state
            : combatSession.DetachedItemState;
    }

    // A batch builds on the given committed state, or on the session's current one.
    private bool TryCreateBatch(
        PlayerCombatSession combatSession,
        CharacterItemState? committedState,
        out AmmunitionBatch? batch)
    {
        batch = null;
        var state = committedState ?? ResolveItemState(combatSession);
        if (state is null)
        {
            return false;
        }

        var stack = state.FindEquipped(ItemEquipmentSlotIds.Ammunition);
        if (stack is null || stack.Quantity <= 0)
        {
            return false;
        }

        batch = new AmmunitionBatch(
            Guid.NewGuid(),
            stack.ItemInstanceId,
            stack.Revision,
            state.ItemStateRevision,
            Math.Min(combatSession.PendingShots, stack.Quantity));
        return true;
    }

    private SimulationAmmunitionConsumptionRequest CreateBatchRequest(
        PlayerCombatSession combatSession,
        AmmunitionBatch batch)
    {
        var session = combatSession.Session;
        return new SimulationAmmunitionConsumptionRequest(
            batch.OperationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            batch.ExpectedCharacterRevision,
            batch.ItemInstanceId,
            batch.ExpectedItemRevision,
            batch.Quantity);
    }

    private void SendBatch(PlayerCombatSession combatSession, AmmunitionBatch batch)
    {
        batch.Attempts++;
        operationTracker.Track(operations.ConsumeAmmunitionAsync(
            this,
            combatSession.Context,
            combatSession,
            batch,
            CreateBatchRequest(combatSession, batch)));
    }

    private void CompleteBatch(AmmunitionBatchCompleted completed)
    {
        var combatSession = completed.CombatSession;
        if (!ReferenceEquals(combatSession.InFlightBatch, completed.Batch))
        {
            return;
        }

        var batch = completed.Batch;
        var result = completed.Result;
        if (result.Succeeded)
        {
            combatSession.InFlightBatch = null;
            metrics?.RecordAmmunitionBatch("committed");
            if (ApplyCommittedState(combatSession, result.Value!, "The committed arrow batch diverged from the active simulation state.", out var current))
            {
                combatSession.RecordBatchRevision(
                    batch.ExpectedCharacterRevision,
                    result.Value!.CharacterRevisions[0].Revision);
                SendQuantityChanged(combatSession, batch, current!);
                OwnerStateChanged?.Invoke(combatSession);
            }

            ReleaseLane(combatSession);
            return;
        }

        var error = result.Error!;
        if (GameServiceOutcome.IsUncertain(error.Code) && batch.Attempts < MaximumBatchAttempts)
        {
            metrics?.RecordAmmunitionBatch("retried");
            batch.RetryAtTick = clock.ServerTick + Backoff(batch.Attempts);
            return;
        }

        combatSession.InFlightBatch = null;
        if (GameServiceOutcome.EndsItemSession(error.Code))
        {
            metrics?.RecordAmmunitionBatch("session_ended");
            metrics?.RecordDroppedShots(batch.Quantity + combatSession.PendingShots);
            combatSession.PendingShots = 0;
            EndSession(combatSession, error.Code, error.Message);
            return;
        }

        // The stack changed in a way the worker did not expect. Its next state comes with
        // the next committed operation or heartbeat; the batch's shots are lost.
        metrics?.RecordAmmunitionBatch("rejected");
        metrics?.RecordDroppedShots(batch.Quantity);
        logger.LogWarning(
            "[SIMULATION] GameService rejected {Quantity} spent arrows of simulation session {SimulationSessionId} with {Code}: {Message}",
            batch.Quantity,
            combatSession.Session.SimulationSessionId,
            error.Code,
            error.Message);
        OwnerStateChanged?.Invoke(combatSession);
        ReleaseLane(combatSession);
    }

    private void StartDeath(PlayerCombatSession combatSession)
    {
        var death = combatSession.Death;
        if (death is null || death.Committed || death.InFlight)
        {
            ReleaseLane(combatSession);
            return;
        }

        death.Queued = false;
        SendDeath(combatSession, death);
    }

    private void SendDeath(PlayerCombatSession combatSession, PlayerDeathAttempt death)
    {
        death.InFlight = true;
        death.Attempts++;
        operationTracker.Track(operations.ProcessDeathAsync(
            this,
            combatSession.Context,
            combatSession,
            death,
            CreateDeathRequest(combatSession, death)));
    }

    private SimulationPlayerDeathRequest CreateDeathRequest(
        PlayerCombatSession combatSession,
        PlayerDeathAttempt death)
    {
        var session = combatSession.Session;
        var state = ResolveItemState(combatSession);
        SimulationDeathAmmunitionRequest? ammunition = null;
        var stack = state?.FindEquipped(ItemEquipmentSlotIds.Ammunition);
        if (combatSession.PendingShots > 0 && stack is { Quantity: > 0 })
        {
            ammunition = new SimulationDeathAmmunitionRequest(
                stack.ItemInstanceId,
                stack.Revision,
                Math.Min(combatSession.PendingShots, stack.Quantity));
        }

        var halfYawRadians = death.YawDegrees * Math.PI / 360d;
        return new SimulationPlayerDeathRequest(
            ScopedGuid.Create("player-death-operation", death.DeathEventId),
            death.DeathEventId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            state?.ItemStateRevision ?? session.CarryState.ItemStateRevision,
            death.PositionX,
            death.PositionY,
            death.PositionZ,
            0d,
            Math.Sin(halfYawRadians),
            0d,
            Math.Cos(halfYawRadians),
            PlayerCorpsePresentationKey,
            ammunition,
            death.Killer);
    }

    private void CompleteDeath(PlayerDeathCompleted completed)
    {
        var combatSession = completed.CombatSession;
        var death = completed.Attempt;
        if (!ReferenceEquals(combatSession.Death, death) || !death.InFlight)
        {
            return;
        }

        death.InFlight = false;
        var result = completed.Result;
        if (result.Succeeded)
        {
            death.Committed = true;
            metrics?.RecordDeathPartition("committed");
            var consumed = completed.Request.AmmunitionConsumption?.Quantity ?? 0;
            combatSession.PendingShots = Math.Max(0, combatSession.PendingShots - consumed);
            corpseStore.Apply(result.Value!.Corpse);
            if (result.Value.CharacterState is { } state)
            {
                var committedState = CharacterItemState.From(state);
                if (combatSession.Detached)
                {
                    combatSession.DetachedItemState = committedState;
                }
                else if (CommittedCarryApplier.IsDiverged(applier.Apply(combatSession.Session, committedState, out _)))
                {
                    EndSession(
                        combatSession,
                        "item_state_diverged",
                        "The committed death diverged from the active simulation state.");
                    return;
                }
            }

            ApplyLatestCarry(combatSession);
            OwnerStateChanged?.Invoke(combatSession);
            ReleaseLane(combatSession);
            return;
        }

        var error = result.Error!;
        if (GameServiceOutcome.EndsSession(error.Code))
        {
            metrics?.RecordDeathPartition("session_ended");
            death.Committed = true;
            combatSession.PendingShots = 0;
            EndSession(combatSession, error.Code, error.Message);
            return;
        }

        // The death keeps its event id and retries while the character stays dead. Shots
        // whose stack changed underneath are dropped, so they cannot block the death.
        if (error.Code is "item_state_conflict" or "ammunition_not_equipped" or "item_quantity_changed"
            && completed.Request.AmmunitionConsumption is not null)
        {
            DropPendingShots(combatSession, "death_ammunition_conflict");
        }

        metrics?.RecordDeathPartition(GameServiceOutcome.IsUncertain(error.Code) ? "retried" : "rejected");
        logger.LogWarning(
            "[SIMULATION] The death of simulation session {SimulationSessionId} did not commit on attempt {Attempt} with {Code}: {Message}",
            combatSession.Session.SimulationSessionId,
            death.Attempts,
            error.Code,
            error.Message);
        death.RetryAtTick = clock.ServerTick + Backoff(death.Attempts);
    }

    // A detached session commits what is left one operation at a time and is then
    // released, or leaves if the player asked to.
    private void DriveDetached(PlayerCombatSession combatSession)
    {
        var context = combatSession.Context;
        if (context.AuthorityOperationInFlight
            || combatSession.InFlightBatch is not null
            || combatSession.Death is { InFlight: true }
            || combatSession.Death is { RetryAtTick: not null })
        {
            return;
        }

        if (combatSession.Death is { Committed: false } death)
        {
            context.AuthorityOperationInFlight = true;
            death.Queued = false;
            SendDeath(combatSession, death);
            return;
        }

        if (combatSession.PendingShots > 0)
        {
            if (TryCreateBatch(combatSession, null, out var batch))
            {
                context.AuthorityOperationInFlight = true;
                combatSession.InFlightBatch = batch;
                combatSession.PendingShots -= batch!.Quantity;
                SendBatch(combatSession, batch);
                return;
            }

            DropPendingShots(combatSession, "ammunition_missing");
        }

        sessions.Remove(combatSession.Session.SimulationSessionId);
        sessionOperations.ReleaseDetachedInBackground(combatSession.Session);
    }

    private void FinishLeave(RealtimePeerContext context, PlayerCombatSession combatSession)
    {
        context.AuthorityOperationInFlight = false;
        if (combatSession.HasDurableWork && combatSession.Death is not null)
        {
            // A death that waits for a retry keeps the leave behind it.
            scheduler.Enqueue(context, new QueuedLeave(this, combatSession));
            return;
        }

        sessions.Remove(combatSession.Session.SimulationSessionId);
        if (context.Session is not null && peers.TryGetCurrentPeer(context, out _))
        {
            StartLeave?.Invoke(context, context.Session);
        }
    }

    private bool ApplyCommittedState(
        PlayerCombatSession combatSession,
        SimulationItemTransactionResponse transaction,
        string divergedMessage,
        out CharacterItemState? current)
    {
        current = null;
        if (combatSession.Detached && CommittedCarryApplier.TryGetCommittedState(transaction, out var detachedState))
        {
            combatSession.DetachedItemState = detachedState;
            current = detachedState;
            return true;
        }

        if (!CommittedCarryApplier.TryGetCommittedState(transaction, out var committed)
            || CommittedCarryApplier.IsDiverged(applier.Apply(combatSession.Session, committed, out var applied)))
        {
            if (!combatSession.Detached)
            {
                EndSession(combatSession, "item_state_diverged", divergedMessage);
            }

            return false;
        }

        current = applied;
        ApplyLatestCarry(combatSession);
        return true;
    }

    private void SendQuantityChanged(
        PlayerCombatSession combatSession,
        AmmunitionBatch batch,
        CharacterItemState current)
    {
        if (combatSession.Detached || !peers.TryGetCurrentPeer(combatSession.Context, out var peer))
        {
            return;
        }

        var stack = current.Equipment.FirstOrDefault(item => item.ItemInstanceId == batch.ItemInstanceId);
        sender.SendControl(peer, RealtimeProtocol.EncodeOwnerItemQuantityChanged(new RealtimeOwnerItemQuantityChanged(
            RealtimeItemQuantityChangeReason.AmmunitionConsumed,
            batch.ExpectedCharacterRevision,
            RealtimeStateMapper.ToRealtimeCarryState(current.Carry),
            batch.ItemInstanceId,
            stack?.Quantity ?? 0,
            stack?.Revision ?? 0)));
    }

    private void ApplyLatestCarry(PlayerCombatSession combatSession)
    {
        if (!combatSession.Detached
            && entityRegistry.TryGetPlayerByCharacterId(combatSession.Session.CharacterId, out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }
    }

    private void DropPendingShots(PlayerCombatSession combatSession, string reason)
    {
        if (combatSession.PendingShots <= 0)
        {
            return;
        }

        metrics?.RecordDroppedShots(combatSession.PendingShots);
        logger.LogInformation(
            "[SIMULATION] Dropped {Quantity} uncommitted shots of simulation session {SimulationSessionId}: {Reason}.",
            combatSession.PendingShots,
            combatSession.Session.SimulationSessionId,
            reason);
        combatSession.PendingShots = 0;
    }

    private void EndSession(PlayerCombatSession combatSession, string code, string message)
    {
        sessions.Remove(combatSession.Session.SimulationSessionId);
        combatSession.Context.AuthorityOperationInFlight = false;
        if (combatSession.Detached)
        {
            sessionOperations.ReleaseDetachedInBackground(combatSession.Session);
            return;
        }

        if (peers.TryGetCurrentPeer(combatSession.Context, out var peer)
            && combatSession.Context.Session is not null)
        {
            teardown.TerminateForAuthorityFailure(
                peer,
                combatSession.Context,
                combatSession.Context.Session,
                code,
                message);
        }
    }

    private void ReleaseLane(PlayerCombatSession combatSession)
    {
        combatSession.Context.AuthorityOperationInFlight = false;
        if (!combatSession.Detached)
        {
            scheduler.StartNext(combatSession.Context);
        }
    }

    // 0.5 s, doubling up to 10 s.
    private uint Backoff(int attempts)
    {
        var seconds = Math.Min(10d, 0.5d * Math.Pow(2d, Math.Max(0, attempts - 1)));
        return Math.Max(1u, (uint)Math.Ceiling(seconds * config.MovementSimulation.TickRateHz));
    }

    private sealed record QueuedAmmunitionBatch(
        PlayerCombatAuthority Owner,
        PlayerCombatSession CombatSession) : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.StartBatch(context, CombatSession);
        }
    }

    private sealed record QueuedPlayerDeath(
        PlayerCombatAuthority Owner,
        PlayerCombatSession CombatSession) : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.StartDeath(CombatSession);
        }
    }

    private sealed record QueuedLeave(
        PlayerCombatAuthority Owner,
        PlayerCombatSession CombatSession) : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.FinishLeave(context, CombatSession);
        }
    }
}
