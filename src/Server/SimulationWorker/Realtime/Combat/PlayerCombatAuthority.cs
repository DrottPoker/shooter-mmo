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
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Realtime;

// Commits a character's spent arrows, its death, and the kills credited to it, for its experience
// and its quests, through the character's authority lane, so they never race its item, corpse,
// and quest operations. A batch starts 5 s after the first uncommitted shot, before any other
// client operation, and before the session ends; a death carries the shots that are still
// uncommitted. A credited kill of a Mob that gives the character experience by its progression, or
// that the character's quest log asks for, commits in kill order, and the experience it granted
// reaches the character's progression. All of them retry with backoff
// after an uncertain answer and outlive the connection: the character's lingering body commits
// them, and what it gathers until it goes, one operation per tick without a peer, and a session
// whose entity went is released only after they committed. A body's committed death ends the
// body. They belong to the session, not to one token of it: a refusal of the credentials that a
// reconnect caused, by rotating the token at GameService, ends nothing, and the work goes again
// with the reconnect's token. While GameService does not answer, none of them is sent: each
// waits, due at once, and goes as soon as GameService answers again. A death or a kill credit
// that does not commit within the retry horizon, counted only while GameService answers, is
// given up. Loop-confined; GameService calls run in PlayerCombatAuthorityOperations.
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
    LingeringBodies bodies,
    SessionAuthorityOperations sessionOperations,
    RealtimeOperationTracker operationTracker,
    PlayerCombatAuthorityOperations operations,
    CombatMetrics? metrics,
    ILogger logger,
    GameServiceAvailability availability,
    QuestOperationExecutor? questOperations = null,
    QuestProgressFilter? questProgress = null,
    ProgressionReplicator? progression = null) : IRealtimeCompletionHandler, ICombatOperationGate
{
    public const string PlayerCorpsePresentationKey = "corpse.remains";

    // The disconnect of a player whose death was given up at the retry horizon. GameService keeps
    // the character as it last committed it.
    public const string DeathNotSavedCode = "death_not_saved";
    public const string DeathNotSavedMessage = "The death could not be saved, so the character continues as it was last saved.";

    // The kinds of the operations that the retry horizon gives up, as logs and metrics name them.
    public const string PlayerDeathOperationKind = "player_death";
    public const string MobKillCreditOperationKind = "mob_kill_credit";

    // An uncertain batch gives up after this many attempts; its arrows stay with the player.
    private const int MaximumBatchAttempts = 8;

    private readonly CommittedCarryApplier applier = new(itemStateStore, sessionStore);
    private readonly Dictionary<Guid, PlayerCombatSession> sessions = [];
    private readonly List<PlayerCombatSession> sessionBuffer = [];

    // Tells the combat system that a death committed or a batch changed the arrow count.
    public Action<PlayerCombatSession>? OwnerStateChanged { get; set; }

    public int SessionCount => sessions.Count;

    // A join attaches the session to its peer; a reconnect, also of a lingering body, moves the
    // work that holds or waits in the previous peer's lane to the new one.
    public PlayerCombatSession Attach(RealtimePeerContext context, ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var existing))
        {
            var previousContext = existing.Context;
            existing.Context = context;
            existing.Session = session;
            existing.Detached = false;
            existing.AwaitingRelease = false;
            existing.DetachedItemState = null;
            if (!ReferenceEquals(previousContext, context))
            {
                MoveLane(existing, previousContext);
            }

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

    // Whether spent arrows, a death, or kill credits of the session have not committed yet; a
    // session whose entity went is released by this authority once they did.
    public bool HoldsDurableWork(Guid simulationSessionId)
    {
        return sessions.TryGetValue(simulationSessionId, out var combatSession) && combatSession.HasDurableWork;
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

    // By the session, since a reconnect may have moved it to another peer while the operation ran.
    public void ClientOperationFinished(ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var combatSession))
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

    // A dying Mob that gives experience, or that a quest asks for, credited these players. Each
    // gets a credit with the Mob's level and experience percent that takes its place in the
    // player's lane at the kill, so no operation that the player sends after the kill, such as
    // abandoning and accepting the quest again, overtakes it. Whether a kill of a Mob that gives no
    // experience counts is decided once the credit's turn comes, by the quest log that every
    // operation before it left, so a kill during an accept still counts for the accepted quest. A
    // lingering body is still in the world, so a kill credits it too; its credits wait in its
    // list, which the combat phase commits in order.
    public void CreditMobKills(WorldActorRuntimeState mob, IReadOnlyList<MobKillCredit> credits)
    {
        ArgumentNullException.ThrowIfNull(mob);
        ArgumentNullException.ThrowIfNull(credits);
        if (questOperations is null
            || questProgress is null
            || (mob.Definition.ExperiencePercent <= 0
                && !questProgress.MayCountKill(mob.Definition.Id, mob.Definition.Tags)))
        {
            return;
        }

        foreach (var credit in credits)
        {
            if (!entityRegistry.TryGetPlayerByCharacterId(credit.CharacterId, out var player)
                || player is null
                || !sessions.TryGetValue(player.Session.SimulationSessionId, out var combatSession)
                || combatSession.AwaitingRelease)
            {
                continue;
            }

            var killCredit = new PendingMobKillCredit(
                Guid.NewGuid(),
                mob.Definition.Id,
                mob.Definition.Tags.ToArray(),
                mob.Definition.Level,
                mob.Definition.ExperiencePercent)
            {
                StartedAt = availability.AvailableTime
            };
            combatSession.KillCredits.Add(killCredit);
            if (!combatSession.Detached)
            {
                scheduler.Enqueue(combatSession.Context, new QueuedMobKillCredit(this, combatSession, killCredit));
            }
        }
    }

    // A character died: every waiting client operation is rejected, and the death runs
    // next in its lane with the shots that are still uncommitted. The kill credits wait behind
    // it in their order. A lingering body has no lane; the combat phase commits its death first.
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
            position.YawDegrees)
        {
            StartedAt = availability.AvailableTime
        };
        if (combatSession.Detached)
        {
            return;
        }

        var context = combatSession.Context;
        var kept = new List<IRealtimeAuthorityOperation>();
        while (context.AuthorityOperationQueue.TryDequeue(out var waiting))
        {
            if (waiting is QueuedAmmunitionBatch)
            {
                combatSession.BatchQueued = false;
                continue;
            }

            if (waiting is QueuedMobKillCredit)
            {
                kept.Add(waiting);
                continue;
            }

            waiting.Cancel(context, RealtimeCombatErrorCodes.CharacterDead, "The character is dead.");
        }

        QueueDeath(combatSession);
        foreach (var operation in kept)
        {
            scheduler.Enqueue(context, operation);
        }
    }

    public void ClearDeath(PlayerCombatSession combatSession)
    {
        combatSession.Death = null;
    }

    // Runs every tick: due batches and retries, and detached sessions that still commit. A due
    // retry waits while GameService does not answer. It allocates nothing on a tick that starts
    // no operation.
    public void ProcessTick()
    {
        if (sessions.Count == 0)
        {
            return;
        }

        sessionBuffer.Clear();
        foreach (var value in sessions.Values)
        {
            sessionBuffer.Add(value);
        }

        var serverTick = clock.ServerTick;
        var batchIntervalTicks = CombatTicks.FromSeconds(
            rules.AmmunitionBatchIntervalSeconds,
            config.MovementSimulation.TickRateHz);
        var gameServiceAvailable = availability.IsAvailable;
        foreach (var combatSession in sessionBuffer)
        {
            if (gameServiceAvailable
                && combatSession.InFlightBatch is { RetryAtTick: { } batchRetry }
                && CombatTicks.IsReached(serverTick, batchRetry))
            {
                combatSession.InFlightBatch.RetryAtTick = null;
                SendBatch(combatSession, combatSession.InFlightBatch);
            }

            if (gameServiceAvailable
                && combatSession.Death is { RetryAtTick: { } deathRetry } death
                && CombatTicks.IsReached(serverTick, deathRetry))
            {
                death.RetryAtTick = null;
                SendDeath(combatSession, death);
            }

            if (gameServiceAvailable
                && combatSession.InFlightKillCredit is { RetryAtTick: { } creditRetry } credit
                && CombatTicks.IsReached(serverTick, creditRetry))
            {
                credit.RetryAtTick = null;
                SendKillCredit(combatSession, credit);
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

        if (metrics is not null)
        {
            var pendingShots = 0;
            foreach (var value in sessions.Values)
            {
                pendingShots += value.PendingShots;
            }

            metrics.SetPendingShots(pendingShots);
        }
    }

    // The connection ended while the session goes on, so the character's lingering body commits
    // the work without a peer: the spent arrows at once, then whatever it gathers until it goes.
    // The peer's lane was cleared, so the combat phase drives the work that waited in it. A body
    // does not attack, and the client operation that may still run no longer tells it when it
    // ends, so attacks no longer wait for it. A body whose death committed already ends at once.
    public void Detach(ActiveSimulationSession session)
    {
        if (!sessions.TryGetValue(session.SimulationSessionId, out var combatSession))
        {
            return;
        }

        combatSession.Detached = true;
        combatSession.BatchQueued = false;
        combatSession.EquipmentChangePending = false;
        if (combatSession.Death is { InFlight: false, RetryAtTick: null } death)
        {
            death.Queued = false;
        }

        if (combatSession.Death is { Committed: true })
        {
            bodies.RequestEnd(session);
        }
    }

    // A reconnect renewed the session of a lingering body without binding a peer, so the work
    // goes on with the new generation's credentials.
    public void RenewSession(ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var combatSession))
        {
            combatSession.Session = session;
        }
    }

    // The entity of the session goes. Work that has not committed keeps the session, with the
    // item state that the store gives up now; it is released once the work is done.
    public bool TryDeferRelease(ActiveSimulationSession session)
    {
        if (!sessions.TryGetValue(session.SimulationSessionId, out var combatSession))
        {
            return false;
        }

        if (!combatSession.HasDurableWork)
        {
            sessions.Remove(session.SimulationSessionId);
            return false;
        }

        combatSession.Detached = true;
        combatSession.AwaitingRelease = true;
        combatSession.BatchQueued = false;
        itemStateStore.TryGetItemState(session.CharacterId, session.SimulationSessionId, out var itemState);
        combatSession.DetachedItemState = itemState;
        if (combatSession.Death is { InFlight: false, RetryAtTick: null } death)
        {
            death.Queued = false;
        }

        return true;
    }

    // The entity went. A session that waits for its release keeps its work; any other session's
    // work ends with it, because the session itself ended.
    public void OnSessionEnded(ActiveSimulationSession session)
    {
        if (sessions.TryGetValue(session.SimulationSessionId, out var combatSession)
            && !combatSession.AwaitingRelease)
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
            case MobKillCreditCompleted credit:
                CompleteKillCredit(credit);
                break;
        }
    }

    // A worker that stops commits the uncommitted shots it can still name exactly and gives a death
    // that has not committed one more attempt, with the operation id of every retry. A batch whose
    // answer arrived but was never finalized is settled first, so the shots behind a committed
    // batch commit on top of its state. A batch without a committed answer is left alone, and its
    // arrows stay with the player. The Mob kill credits that have not committed get one more
    // attempt each. While GameService does not answer, nothing is sent, since every attempt would
    // only spend its timeout, and the shots, credits, and deaths are lost: a character whose death
    // was lost returns alive where it last was saved alive.
    public async Task FlushOnShutdownAsync(IReadOnlyList<RealtimeOperationResult> unfinalized)
    {
        ArgumentNullException.ThrowIfNull(unfinalized);
        if (!availability.IsAvailable)
        {
            if (sessions.Count > 0)
            {
                logger.LogWarning(
                    "[SIMULATION] GameService does not answer, so the uncommitted shots and Mob kill credits of {Count} sessions are not committed at shutdown.",
                    sessions.Count);
            }

            return;
        }

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
            if (combatSession.Death is { Committed: false } death)
            {
                flushes.Add(FlushDeathAsync(combatSession, death, unfinalized));
            }
        }

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

        if (questOperations is not null)
        {
            foreach (var combatSession in sessions.Values)
            {
                var credits = new List<PendingMobKillCredit>();
                if (combatSession.InFlightKillCredit is { } inFlight && !IsSettled(inFlight, unfinalized))
                {
                    credits.Add(inFlight);
                }

                credits.AddRange(combatSession.KillCredits);
                combatSession.KillCredits.Clear();
                if (credits.Count > 0)
                {
                    flushes.Add(FlushKillCreditsAsync(combatSession, credits, questOperations));
                }
            }
        }

        await Task.WhenAll(flushes);
    }

    // A death that has not committed gets one more attempt, with the operation id every retry uses,
    // so a death that did commit replays its result. One whose answer arrived and committed needs
    // none. A death that commits is saved as the respawn leaves the character, in the last
    // checkpoint that follows; one that does not leaves the character as it last was alive, with
    // its items, because no save says that it respawned.
    private async Task FlushDeathAsync(
        PlayerCombatSession combatSession,
        PlayerDeathAttempt death,
        IReadOnlyList<RealtimeOperationResult> unfinalized)
    {
        if (unfinalized.OfType<PlayerDeathCompleted>().Any(
                completed => ReferenceEquals(completed.Attempt, death) && completed.Result.Succeeded))
        {
            death.Committed = true;
            return;
        }

        var session = combatSession.Session;
        var result = await operations.ProcessDeathForShutdownAsync(
            session.SimulationSessionId,
            CreateDeathRequest(combatSession, death));
        if (result.Succeeded)
        {
            death.Committed = true;
            return;
        }

        logger.LogWarning(
            "[SIMULATION] The death of simulation session {SimulationSessionId} did not commit at shutdown with {Code}, so the character continues as it was last saved alive.",
            session.SimulationSessionId,
            result.Error!.Code);
    }

    // A credit whose answer arrived committed or rejected needs no further attempt.
    private static bool IsSettled(PendingMobKillCredit credit, IReadOnlyList<RealtimeOperationResult> unfinalized)
    {
        return unfinalized
            .OfType<MobKillCreditCompleted>()
            .Any(completed => ReferenceEquals(completed.Credit, credit) && !completed.Outcome.Uncertain);
    }

    // A worker that stops sends the kills that it credited and that have not committed, in kill
    // order, with the operation id of each, so a credit that did commit replays its result. Each
    // credit of a Mob that gives no experience is decided right before it goes, by the quest log
    // that the credits before it left, as in the lane.
    private async Task FlushKillCreditsAsync(
        PlayerCombatSession combatSession,
        IReadOnlyList<PendingMobKillCredit> credits,
        QuestOperationExecutor executor)
    {
        foreach (var credit in credits)
        {
            if (!ShouldSend(combatSession, credit))
            {
                continue;
            }

            try
            {
                var outcome = await executor.ExecuteAsync(
                    combatSession.Session,
                    credit.ToOperation(),
                    CancellationToken.None);
                if (outcome.ShouldDisconnect)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "[SIMULATION] Unhandled error while crediting a Mob kill of simulation session {SimulationSessionId} on shutdown.",
                    combatSession.Session.SimulationSessionId);
            }
        }
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

    // A credit that a disconnect already sent has left the session's credits, and one of a Mob
    // that gives no experience and that the quest log does not ask for costs no GameService call.
    private void StartKillCredit(PlayerCombatSession combatSession, PendingMobKillCredit credit)
    {
        if (!combatSession.KillCredits.Remove(credit) || !ShouldSend(combatSession, credit))
        {
            ReleaseLane(combatSession);
            return;
        }

        combatSession.InFlightKillCredit = credit;
        SendKillCredit(combatSession, credit);
    }

    // A kill of a Mob that gives experience goes unless the character's progression says that the
    // Mob gives it none: at the highest level, or for a Mob too low for it. A session whose entity
    // went left its quest log and its progression with it, so GameService decides its credits.
    private bool ShouldSend(PlayerCombatSession combatSession, PendingMobKillCredit credit)
    {
        return (credit.MobExperiencePercent > 0
                && (progression?.GivesExperience(combatSession.Session.CharacterId, credit.MobLevel) ?? true))
            || combatSession.AwaitingRelease
            || questProgress!.CountsKill(combatSession.Session, credit.MobActorId, credit.MobTags);
    }

    // While GameService does not answer, the credit holds its place, due at once.
    private void SendKillCredit(PlayerCombatSession combatSession, PendingMobKillCredit credit)
    {
        if (!availability.IsAvailable)
        {
            credit.RetryAtTick = clock.ServerTick;
            return;
        }

        credit.Attempts++;
        credit.SentWith = combatSession.Session;
        operationTracker.Track(operations.CreditMobKillAsync(
            this,
            combatSession.Context,
            combatSession,
            credit,
            questOperations!));
    }

    // A committed credit patches the client's quest log when it changed a quest. An uncertain one
    // keeps the lane and asks again with the same operation id until it commits, like a death, so
    // no experience or progress is lost and no later quest change overtakes it. A rejection can
    // never commit and is dropped.
    private void CompleteKillCredit(MobKillCreditCompleted completed)
    {
        var combatSession = completed.CombatSession;
        var credit = completed.Credit;
        if (!ReferenceEquals(combatSession.InFlightKillCredit, credit))
        {
            return;
        }

        var outcome = completed.Outcome;
        ApplyProgression(combatSession, outcome);
        SendQuestLogChange(combatSession, outcome.QuestLogChange);
        var waitsForReconnect = !outcome.Succeeded
            && outcome.ShouldDisconnect
            && WaitsForReconnect(credit.SentWith, outcome.Code, outcome.Message);
        if (!outcome.Succeeded && outcome.ShouldDisconnect && !waitsForReconnect)
        {
            combatSession.InFlightKillCredit = null;
            combatSession.KillCredits.Clear();
            EndSession(combatSession, outcome.Code, outcome.Message);
            return;
        }

        if (!outcome.Succeeded)
        {
            logger.LogWarning(
                "[SIMULATION] A {MobActorId} kill was not credited to simulation session {SimulationSessionId} on attempt {Attempt} with {Code}: {Message}",
                credit.MobActorId,
                combatSession.Session.SimulationSessionId,
                credit.Attempts,
                outcome.Code,
                outcome.Message);
            if (outcome.Uncertain || waitsForReconnect)
            {
                if (IsPastRetryHorizon(credit.StartedAt))
                {
                    AbandonKillCredit(combatSession, credit);
                    return;
                }

                credit.RetryAtTick = clock.ServerTick + Backoff(credit.Attempts);
                return;
            }
        }

        combatSession.InFlightKillCredit = null;
        ReleaseLane(combatSession);
    }

    // The experience a committed credit granted reaches the character while its entity is in the
    // world, and its owner while a peer plays it.
    private void ApplyProgression(PlayerCombatSession combatSession, QuestOperationOutcome outcome)
    {
        if (progression is null || outcome.Progression is not { } change)
        {
            return;
        }

        IRealtimePeerConnection? owner = null;
        if (!combatSession.Detached && peers.TryGetCurrentPeer(combatSession.Context, out var peer))
        {
            owner = peer;
        }

        progression.Apply(combatSession.Session, owner, change, outcome.ProgressionReason);
    }

    private void SendQuestLogChange(PlayerCombatSession combatSession, RealtimeQuestLogChanged? change)
    {
        if (change is not null
            && !combatSession.Detached
            && peers.TryGetCurrentPeer(combatSession.Context, out var peer))
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeQuestLogChanged(change));
        }
    }

    private void QueueDeath(PlayerCombatSession combatSession)
    {
        combatSession.Death!.Queued = true;
        scheduler.Enqueue(combatSession.Context, new QueuedPlayerDeath(this, combatSession));
    }

    // A reconnect gives the session a new peer, whose lane starts empty. Durable work that is in
    // flight or waits for a retry keeps holding the lane, and its completion opens the new one.
    // A client operation that still runs on the old peer's lane keeps the new lane closed until
    // it ends, so a credit that waited behind an accept still sees the accepted quest. The work
    // that waited in the old peer's lane takes its place in the new one: the death first, then
    // the kill credits in kill order. A batch is queued again when it is due.
    private void MoveLane(PlayerCombatSession combatSession, RealtimePeerContext previousContext)
    {
        combatSession.BatchQueued = false;
        if (combatSession.InFlightBatch is not null
            || combatSession.Death is { InFlight: true } or { RetryAtTick: not null }
            || combatSession.InFlightKillCredit is not null)
        {
            combatSession.Context.AuthorityOperationInFlight = true;
        }
        else if (previousContext.AuthorityOperationInFlight)
        {
            AuthorityOperationScheduler.HandOver(previousContext, combatSession.Context);
        }

        if (combatSession.Death is { Committed: false, InFlight: false, RetryAtTick: null })
        {
            QueueDeath(combatSession);
        }

        // A credit may start, and leave the list, while the next one is queued.
        foreach (var credit in combatSession.KillCredits.ToArray())
        {
            scheduler.Enqueue(combatSession.Context, new QueuedMobKillCredit(this, combatSession, credit));
        }
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

    // A session whose entity went left the store with it, so it keeps its own copy.
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

    // While GameService does not answer, the batch holds its place, due at once.
    private void SendBatch(PlayerCombatSession combatSession, AmmunitionBatch batch)
    {
        if (!availability.IsAvailable)
        {
            batch.RetryAtTick = clock.ServerTick;
            return;
        }

        batch.Attempts++;
        batch.SentWith = combatSession.Session;
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
        if (batch.Attempts < MaximumBatchAttempts
            && (GameServiceOutcome.IsUncertain(error.Code)
                || WaitsForReconnect(batch.SentWith, error.Code, error.Message)))
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

    // While GameService does not answer, the death holds its place, due at once.
    private void SendDeath(PlayerCombatSession combatSession, PlayerDeathAttempt death)
    {
        if (!availability.IsAvailable)
        {
            death.RetryAtTick = clock.ServerTick;
            return;
        }

        death.InFlight = true;
        death.Attempts++;
        death.SentWith = combatSession.Session;
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
                if (combatSession.AwaitingRelease)
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

            // A lingering body never respawns: its committed death ends it.
            if (combatSession.Detached)
            {
                bodies.RequestEnd(combatSession.Session);
            }

            ReleaseLane(combatSession);
            return;
        }

        var error = result.Error!;
        var waitsForReconnect = WaitsForReconnect(death.SentWith, error.Code, error.Message);
        if (GameServiceOutcome.EndsSession(error.Code) && !waitsForReconnect)
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

        if (IsPastRetryHorizon(death.StartedAt))
        {
            AbandonDeath(combatSession, death, error.Code);
            return;
        }

        metrics?.RecordDeathPartition(
            GameServiceOutcome.IsUncertain(error.Code) || waitsForReconnect ? "retried" : "rejected");
        logger.LogWarning(
            "[SIMULATION] The death of simulation session {SimulationSessionId} did not commit on attempt {Attempt} with {Code}: {Message}",
            combatSession.Session.SimulationSessionId,
            death.Attempts,
            error.Code,
            error.Message);
        death.RetryAtTick = clock.ServerTick + Backoff(death.Attempts);
    }

    // A detached session commits what is left one operation at a time: the death first, then the
    // spent arrows, then the kill credits in kill order. A session whose entity went is then
    // released; a lingering body waits for what it gathers next, or for its end.
    private void DriveDetached(PlayerCombatSession combatSession)
    {
        var context = combatSession.Context;
        if (context.AuthorityOperationInFlight
            || combatSession.InFlightBatch is not null
            || combatSession.Death is { InFlight: true }
            || combatSession.Death is { RetryAtTick: not null }
            || combatSession.InFlightKillCredit is not null)
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

        // A credit of a Mob that gives no experience and that the quest log does not ask for costs
        // no GameService call, as in the lane.
        while (combatSession.KillCredits.Count > 0)
        {
            var credit = combatSession.KillCredits[0];
            combatSession.KillCredits.RemoveAt(0);
            if (!ShouldSend(combatSession, credit))
            {
                continue;
            }

            context.AuthorityOperationInFlight = true;
            combatSession.InFlightKillCredit = credit;
            SendKillCredit(combatSession, credit);
            return;
        }

        if (!combatSession.AwaitingRelease)
        {
            return;
        }

        sessions.Remove(combatSession.Session.SimulationSessionId);
        sessionOperations.ReleaseDetachedInBackground(combatSession.Session);
    }

    // A committed state goes to the store while the entity is in the world, also a lingering
    // body's, so its next operations, such as its death, build on it.
    private bool ApplyCommittedState(
        PlayerCombatSession combatSession,
        SimulationItemTransactionResponse transaction,
        string divergedMessage,
        out CharacterItemState? current)
    {
        current = null;
        if (combatSession.AwaitingRelease && CommittedCarryApplier.TryGetCommittedState(transaction, out var detachedState))
        {
            combatSession.DetachedItemState = detachedState;
            current = detachedState;
            return true;
        }

        if (!CommittedCarryApplier.TryGetCommittedState(transaction, out var committed)
            || CommittedCarryApplier.IsDiverged(applier.Apply(combatSession.Session, committed, out var applied)))
        {
            if (!combatSession.AwaitingRelease)
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
        if (!combatSession.AwaitingRelease
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

    // The session cannot go on. A session whose entity went is released; a lingering body's
    // session ends here, so the body goes at once; a connected player is disconnected.
    private void EndSession(PlayerCombatSession combatSession, string code, string message)
    {
        sessions.Remove(combatSession.Session.SimulationSessionId);
        scheduler.Release(combatSession.Context);
        if (combatSession.AwaitingRelease)
        {
            sessionOperations.ReleaseDetachedInBackground(combatSession.Session);
            return;
        }

        if (combatSession.Detached)
        {
            var session = combatSession.Session;
            sessionStore.Invalidate(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken,
                code,
                message);
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
        scheduler.Release(combatSession.Context);
        if (!combatSession.Detached)
        {
            scheduler.StartNext(combatSession.Context);
        }
    }

    // GameService refused the credentials of the generation that the attempt was sent with. That
    // ends the session, unless the refusal came from a reconnect, whose ticket consumption
    // rotated the token at GameService: the store holds the reconnect's generation already, or a
    // join that may register it is in flight. Then the work keeps its place in the session and
    // goes again after its backoff, with the reconnect's token once its join attached the session;
    // a session that ends after all takes its work with it, as any ended session does.
    private bool WaitsForReconnect(ActiveSimulationSession? sentWith, string code, string message)
    {
        return sentWith is not null
            && GameServiceOutcome.RefusesSessionCredentials(code)
            && sessionStore.TryGet(sentWith.CharacterId, out var current)
            && current!.SimulationSessionId == sentWith.SimulationSessionId
            && sessionStore.RefuseCredentials(
                sentWith.CharacterId,
                sentWith.SimulationSessionId,
                sentWith.SimulationSessionToken,
                code,
                message) != SessionCredentialsRefusal.Ended;
    }

    // Whether an operation that started at this available time was retried for the whole horizon
    // while GameService answered.
    private bool IsPastRetryHorizon(TimeSpan startedAt)
    {
        return availability.AvailableTime - startedAt >= config.UncertainOperationRetryHorizon;
    }

    // The kill grants nothing and counts for no quest; the credits behind it go on.
    private void AbandonKillCredit(PlayerCombatSession combatSession, PendingMobKillCredit credit)
    {
        availability.RecordAbandonedOperation(MobKillCreditOperationKind);
        logger.LogError(
            "[SIMULATION] Gave up the {OperationKind} operation {OperationId} of simulation session {SimulationSessionId} after {Attempts} attempts: its outcome stayed uncertain for the retry horizon of {Horizon} while GameService answered, so the {MobActorId} kill is not credited.",
            MobKillCreditOperationKind,
            credit.OperationId,
            combatSession.Session.SimulationSessionId,
            credit.Attempts,
            config.UncertainOperationRetryHorizon,
            credit.MobActorId);
        combatSession.InFlightKillCredit = null;
        ReleaseLane(combatSession);
    }

    // The death never committed: the session ends, and it is released without a final state, so
    // the character is free at once and keeps what GameService committed last.
    private void AbandonDeath(PlayerCombatSession combatSession, PlayerDeathAttempt death, string code)
    {
        availability.RecordAbandonedOperation(PlayerDeathOperationKind);
        metrics?.RecordDeathPartition("abandoned");
        logger.LogError(
            "[SIMULATION] Gave up the {OperationKind} operation {OperationId} of simulation session {SimulationSessionId} after {Attempts} attempts, the last with {Code}: it did not commit within the retry horizon of {Horizon} while GameService answered, so the session ends with {DisconnectCode}.",
            PlayerDeathOperationKind,
            ScopedGuid.Create("player-death-operation", death.DeathEventId),
            combatSession.Session.SimulationSessionId,
            death.Attempts,
            code,
            config.UncertainOperationRetryHorizon,
            DeathNotSavedCode);
        death.Committed = true;
        combatSession.PendingShots = 0;
        var session = combatSession.Session;
        var releasedByEnd = combatSession.AwaitingRelease;
        EndSession(combatSession, DeathNotSavedCode, DeathNotSavedMessage);
        if (!releasedByEnd)
        {
            sessionOperations.ReleaseDetachedInBackground(session);
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

    private sealed record QueuedMobKillCredit(
        PlayerCombatAuthority Owner,
        PlayerCombatSession CombatSession,
        PendingMobKillCredit Credit) : IRealtimeAuthorityOperation
    {
        public void Start(RealtimePeerContext context, PlayerSimulationEntity entity, PlayerMovementState state)
        {
            Owner.StartKillCredit(CombatSession, Credit);
        }
    }
}
