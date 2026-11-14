using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.StackStressGenerator;

// The shared surface of every stress bot that the run coordinator polls.
public interface IStressBot : IDisposable
{
    StressBotState State { get; }

    bool IsGameplayIdle { get; }

    void Start();

    void Poll(long nowTimestamp, CancellationToken cancellationToken);

    void BeginLeave(long nowTimestamp);

    void BeginGameplayDrain();

    void BeginRemoteCadenceWindow(long nowTimestamp);

    void EndRemoteCadenceWindow(long nowTimestamp);

    StressBotSnapshot Capture();

    // Safe to call from another thread than the one that polls the bot.
    StressBotProgress CaptureProgress();

    // Whether the bot holds a simulation session now, for the drift checks. Safe to read from
    // another thread than the one that polls the bot.
    StressSessionHolding SessionHolding { get; }
}

// Whether a bot holds a simulation session: certainly, as a joined bot that plays does; possibly,
// as a bot on its way into or out of the game does, whose body may still stand or whose session
// may already exist; or not, as a bot that failed or never started.
public readonly record struct StressSessionHolding(bool Certainly, bool Possibly)
{
    public static StressSessionHolding Holds { get; } = new(true, true);

    public static StressSessionHolding Maybe { get; } = new(false, true);

    public static StressSessionHolding None { get; } = new(false, false);

    // A connection's own state: joined holds a session; connecting, joining, leaving, and a
    // completed leave, whose body still stands, may hold one.
    public static StressSessionHolding Of(StressBotState state)
    {
        return state switch
        {
            StressBotState.Joined => Holds,
            StressBotState.Connecting or StressBotState.Joining or StressBotState.Leaving or StressBotState.Completed => Maybe,
            _ => None
        };
    }
}

// The phases of a playing bot's churn.
public enum PlayingChurnPhase
{
    None,

    // The role plays on while the bot waits for a safe moment to leave.
    Waiting,

    // The bot walks away from aggressive Mobs before it leaves.
    Retreating,

    // The bot stands still before it leaves.
    Settling,

    // The leave was sent and waits for its acceptance.
    Leaving,

    // The bot is out of the game and takes the way back.
    Away,

    // The new connection joins, and the bot checks what it finds.
    Rejoining
}

public readonly record struct StressBotProgress(
    StressBotState State,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots)
{
    public static StressBotProgress From(SimulationBotProgress progress)
    {
        return new StressBotProgress(
            (StressBotState)progress.State,
            progress.SnapshotsReceived,
            progress.EstimatedMissingSnapshots);
    }
}

// What the playing bots of one run share: their assignments, the item catalog they read like a
// client, the navigation, and the metrics.
public sealed record PlayingRunContext(
    PlayingScenario Scenario,
    IReadOnlyDictionary<int, PlayingAssignment> Assignments,
    PlayingGameData GameData,
    SimulationBotNavigation? Navigation,
    PlayingMetrics Metrics)
{
    // The way back of a churning bot, which the admission provider walks, or null when the run
    // has no churn.
    public Func<PlayingRejoinPlan, CancellationToken, Task<PlayingRejoinTicket>>? Rejoin { get; init; }

    // What the database says about a body that is gone, for a join that found it gone although
    // its ticket still named it, because its release came between the two.
    public Func<PlayingRejoinPlan, CancellationToken, Task<PlayingRejoinFacts>>? ReadRejoinFacts { get; init; }

    // How fast a body regenerates, for the rejoin check.
    public PlayingRegeneration Regeneration { get; init; } = new(1, 1);
}

// One bot of the playing workload: a headless client with a world view, the body that sends
// its intents like a Unity client and measures them, and the state machine of its role, which
// thinks every few hundred milliseconds. When the churn asks it to, the bot leaves the game like
// a player who logs out and comes back: it waits for a safe moment, walks away from aggressive
// Mobs first when it must, stands, leaves, takes the way back with a new connection and a new
// world view, checks what it finds, and plays its role on.
public sealed class PlayingStressBot : IStressBot, ISimulationBotRealtimeObserver, ISimulationBotInputSource
{
    // A decision follows the previous one after this long, drawn per bot from the seed.
    public static readonly TimeSpan MinimumThinkDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumThinkDelay = TimeSpan.FromMilliseconds(650);

    // A bot waits this long at most for a safe moment to leave, walks away from Mobs this long at
    // most, stands this long before it leaves, and waits this long for its first owner state
    // after the join of its way back.
    public static readonly TimeSpan ChurnSafetyWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChurnRetreatLimit = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ChurnSettleTime = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan ChurnOwnerStateWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChurnCheckInterval = TimeSpan.FromMilliseconds(100);
    private const int MaximumRetreats = 2;

    private readonly StressGeneratorOptions options;
    private readonly PlayingRunContext context;
    private readonly PlayingAssignment assignment;
    private readonly Action<double> recordInputAcknowledgement;
    private readonly StressGameplayMetrics gameplayMetrics;
    private readonly Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory;
    private readonly IPlayingRole role;
    private readonly Random thinkRandom;
    private readonly long createdTimestamp = Stopwatch.GetTimestamp();
    private readonly int botIndex;
    private readonly string characterName;
    private readonly PlayingKit kit;
    private HeadlessSimulationBotClient client;
    private PlayingBotBody body;
    private volatile bool clientRetired;
    private long nextThinkTimestamp;
    private bool draining;
    private bool cadenceOpen;
    private bool cadenceEnded;

    // What the retired connections of the bot counted, so the bot's numbers cover all of them.
    private long retiredPacketsSent;
    private long retiredBytesSent;
    private long retiredPacketsReceived;
    private long retiredBytesReceived;
    private long retiredSnapshots;
    private long retiredMissingSnapshots;
    private long retiredSpawnPackets;
    private long retiredDespawnPackets;
    private long retiredInputsDropped;
    private uint retiredLatestTick;
    private double? firstJoinLatencyMs;
    private SimulationBotRemoteCadenceSummary? retiredCadence;

    // The churn in progress.
    private volatile PlayingChurnPhase phase = PlayingChurnPhase.None;
    private PlayingChurnEvent? churn;
    private long churnRequestedTimestamp;
    private long phaseTimestamp;
    private long nextChurnCheckTimestamp;
    private long settledSinceTimestamp;
    private long leaveSentTimestamp;
    private long joinedTimestamp;
    private int retreats;
    private bool roleSuspended;
    private PlayingLeaveSnapshot? leave;
    private PlayingRejoinPlan? rejoinPlan;
    private Task<PlayingRejoinTicket>? wayBack;
    private PlayingRejoinTicket? rejoinTicket;
    private Task<PlayingRejoinFacts>? lateFacts;

    // What the bot found right after the join of its way back, kept while late facts are read.
    private RejoinFound? rejoinFound;
    private string? failureCode;
    private string? failureMessage;

    public PlayingStressBot(
        StressGeneratorOptions options,
        StressBotAdmission admission,
        PlayingRunContext context,
        Action<double> recordInputAcknowledgement,
        StressGameplayMetrics gameplayMetrics,
        Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(context);
        this.options = options;
        this.context = context;
        this.recordInputAcknowledgement = recordInputAcknowledgement;
        this.gameplayMetrics = gameplayMetrics;
        this.refreshInventory = refreshInventory;
        botIndex = admission.BotIndex;
        characterName = admission.CharacterName;
        assignment = context.Assignments[admission.BotIndex];
        var world = new SimulationBotWorldView(admission.CharacterName);
        if (admission.Inventory is { } inventory)
        {
            world.Items.Load(PlayingBotBody.ToItemSnapshot(inventory));
        }

        // The kit is what the kit fixture granted, as the bot's inventory shows it.
        kit = PlayingRoster.ResolveKit(
            assignment.Kit,
            world.Items.Items.Select(item => item.DefinitionId),
            context.GameData.ItemCatalog,
            requireWeapon: assignment.Role == PlayingRole.Fighter);
        client = CreateClient(admission, world);
        body = CreateBody(client, world);
        var decisions = new Random(assignment.DecisionSeed);
        thinkRandom = new Random(PlayingRoster.Mix(assignment.DecisionSeed, 0x5448494e));
        var town = context.Scenario.GroundsOf(PlayingGroundKind.Town).FirstOrDefault();
        role = assignment.Role switch
        {
            PlayingRole.Fighter => new FighterRole(
                assignment.Home ?? throw new InvalidOperationException($"Fighter bot {admission.BotIndex} has no hunting ground."),
                context.Scenario.TeleportToGrounds,
                decisions,
                town),
            PlayingRole.Gatherer => new GathererRole(
                assignment.Home ?? throw new InvalidOperationException($"Gatherer bot {admission.BotIndex} has no node field."),
                context.Scenario.TeleportToGrounds,
                decisions,
                town),
            PlayingRole.Trader => new TraderRole(
                assignment.Home ?? throw new InvalidOperationException($"Trader bot {admission.BotIndex} has no town."),
                context.Scenario.TeleportToGrounds,
                decisions),
            PlayingRole.Walker => new WalkerRole(assignment.Home, context.Scenario.TeleportToGrounds, decisions),
            _ => throw new InvalidOperationException($"The role {assignment.Role} is not implemented.")
        };
    }


    // A churning bot is in the game from the run's point of view until its churn failed.
    public StressBotState State => failureCode is not null
        ? StressBotState.Failed
        : phase is PlayingChurnPhase.Leaving or PlayingChurnPhase.Away or PlayingChurnPhase.Rejoining
            ? StressBotState.Joined
            : (StressBotState)Volatile.Read(ref client).State;

    // A bot on its way out of or back into the game is not idle; one that only waits to leave is.
    public bool IsGameplayIdle => body.IsIdle
        && phase is not (PlayingChurnPhase.Leaving or PlayingChurnPhase.Away or PlayingChurnPhase.Rejoining);

    public StressSessionHolding SessionHolding => SessionHoldingOf(
        phase,
        (StressBotState)Volatile.Read(ref client).State,
        failureCode is not null);

    public IPlayingRole Role => role;

    // A bot whose churn failed holds none; a bot on its way out of or back into the game may
    // hold one, its standing body's or its new one; every other bot holds what its connection
    // holds.
    public static StressSessionHolding SessionHoldingOf(PlayingChurnPhase phase, StressBotState connection, bool failed)
    {
        if (failed)
        {
            return StressSessionHolding.None;
        }

        return phase is PlayingChurnPhase.Leaving or PlayingChurnPhase.Away or PlayingChurnPhase.Rejoining
            ? StressSessionHolding.Maybe
            : StressSessionHolding.Of(connection);
    }

    // Whether the bot is busy with a churn, for tests and diagnostics.
    public bool IsChurning => phase != PlayingChurnPhase.None;

    public void Start()
    {
        client.Start();
    }

    public void Poll(long nowTimestamp, CancellationToken cancellationToken)
    {
        if (!clientRetired)
        {
            client.Poll(nowTimestamp);
        }

        body.Update(nowTimestamp, cancellationToken);
        if (phase != PlayingChurnPhase.None)
        {
            AdvanceChurn(nowTimestamp, cancellationToken);
        }

        if (draining
            || roleSuspended
            || phase is not (PlayingChurnPhase.None or PlayingChurnPhase.Waiting)
            || client.State != SimulationBotClientState.Joined
            || nowTimestamp < nextThinkTimestamp)
        {
            return;
        }

        role.Think(body, RoleTime(nowTimestamp));
        body.AfterThink();
        var delay = MinimumThinkDelay
            + TimeSpan.FromTicks((long)((MaximumThinkDelay - MinimumThinkDelay).Ticks * thinkRandom.NextDouble()));
        nextThinkTimestamp = nowTimestamp + Ticks(delay);
    }

    // The churn asks the bot to leave and come back; a bot that is still away from an earlier
    // one or no longer plays skips it. Runs on the bot's poll thread.
    public void RequestChurn(PlayingChurnEvent churnEvent, long nowTimestamp)
    {
        ArgumentNullException.ThrowIfNull(churnEvent);
        context.Metrics.Count(PlayingCounter.ChurnRequests);
        if (phase != PlayingChurnPhase.None
            || draining
            || failureCode is not null
            || context.Rejoin is null
            || client.State != SimulationBotClientState.Joined)
        {
            context.Metrics.Count(PlayingCounter.ChurnSkippedBusy);
            return;
        }

        churn = churnEvent;
        churnRequestedTimestamp = nowTimestamp;
        nextChurnCheckTimestamp = nowTimestamp;
        retreats = 0;
        Enter(PlayingChurnPhase.Waiting, nowTimestamp);
    }

    // The run's own leave at its end; a churn that has not left yet gives way to it.
    public void BeginLeave(long nowTimestamp)
    {
        if (phase is PlayingChurnPhase.Waiting or PlayingChurnPhase.Retreating or PlayingChurnPhase.Settling)
        {
            Skip(PlayingCounter.ChurnSkippedLate, nowTimestamp);
        }

        if (phase == PlayingChurnPhase.None && !clientRetired)
        {
            client.BeginLeave(nowTimestamp);
        }
    }

    public void BeginGameplayDrain()
    {
        draining = true;
        body.Stand();
    }

    public void BeginRemoteCadenceWindow(long nowTimestamp)
    {
        cadenceOpen = true;
        if (!clientRetired)
        {
            client.BeginRemoteCadenceWindow(nowTimestamp);
        }
    }

    public void EndRemoteCadenceWindow(long nowTimestamp)
    {
        cadenceEnded = true;
        if (!clientRetired)
        {
            client.EndRemoteCadenceWindow(nowTimestamp);
        }
    }

    public StressBotSnapshot Capture()
    {
        // A connection that left counts among the retired ones already.
        var snapshot = clientRetired ? Empty(client.Capture()) : client.Capture();
        return new StressBotSnapshot(
            snapshot.BotIndex,
            snapshot.CharacterName,
            State,
            firstJoinLatencyMs ?? snapshot.JoinLatencyMs,
            retiredPacketsSent + snapshot.PacketsSent,
            retiredBytesSent + snapshot.BytesSent,
            retiredPacketsReceived + snapshot.PacketsReceived,
            retiredBytesReceived + snapshot.BytesReceived,
            Interlocked.Read(ref retiredSnapshots) + snapshot.SnapshotsReceived,
            Interlocked.Read(ref retiredMissingSnapshots) + snapshot.EstimatedMissingSnapshots,
            retiredSpawnPackets + snapshot.SpawnPackets,
            retiredDespawnPackets + snapshot.DespawnPackets,
            retiredInputsDropped + snapshot.UnacknowledgedInputsDropped,
            Math.Max(retiredLatestTick, snapshot.LatestServerTick),
            failureCode ?? snapshot.FailureCode,
            failureMessage ?? snapshot.FailureMessage,
            SimulationBotRemoteCadenceSummary.Combine(retiredCadence, snapshot.RemoteCadence));
    }

    public StressBotProgress CaptureProgress()
    {
        var progress = clientRetired ? default : Volatile.Read(ref client).CaptureProgress();
        return new StressBotProgress(
            State,
            Interlocked.Read(ref retiredSnapshots) + progress.SnapshotsReceived,
            Interlocked.Read(ref retiredMissingSnapshots) + progress.EstimatedMissingSnapshots);
    }

    private static SimulationBotClientSnapshot Empty(SimulationBotClientSnapshot snapshot)
    {
        return snapshot with
        {
            JoinLatencyMs = null,
            PacketsSent = 0,
            BytesSent = 0,
            PacketsReceived = 0,
            BytesReceived = 0,
            SnapshotsReceived = 0,
            EstimatedMissingSnapshots = 0,
            SpawnPackets = 0,
            DespawnPackets = 0,
            UnacknowledgedInputsDropped = 0,
            LatestServerTick = 0,
            RemoteCadence = null
        };
    }

    public void Dispose()
    {
        client.Dispose();
    }

    public RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        return body.CurrentPath;
    }

    public void OnJoined(RealtimeJoinAccepted session)
    {
        body.OnJoined(session);
    }

    public void OnItemOperationResult(RealtimeItemOperationResult result)
    {
        body.Complete(
            result.OperationId,
            result.Succeeded,
            result.Error?.Code,
            result.RequiresInventoryRefresh,
            result.CarryState?.ItemStateRevision);
    }

    public void OnCorpsePresence(RealtimeCorpsePresenceSnapshotChunk chunk)
    {
    }

    public void OnCorpseInteractionResult(RealtimeCorpseInteractionResult result)
    {
        body.Complete(
            result.OperationId,
            result.Succeeded,
            result.Error?.Code,
            result.RequiresInventoryRefresh,
            result.CarryState?.ItemStateRevision);
    }

    public void OnCorpseViewState(RealtimeCorpseViewStateChunk chunk)
    {
    }

    public void OnCorpseViewClosed(RealtimeCorpseViewClosed closed)
    {
    }

    public void OnCombatIntentResult(RealtimeCombatIntentResult result)
    {
        body.Complete(result.OperationId, result.Succeeded, result.Error?.Code, false, null);
    }

    public void OnAdminIntentResult(RealtimeAdminIntentResult result)
    {
        body.Complete(result.OperationId, result.Succeeded, result.Error?.Code, false, null);
    }

    public void OnWorldInteractionOpened(RealtimeWorldInteractionOpened opened)
    {
        body.OnInteractionOpened(opened);
    }

    public void OnWorldInteractionResult(RealtimeWorldInteractionResult result)
    {
        // A successful Open is answered by the opened message; a successful Close also sends a
        // result, which completes the close.
        if (result.Succeeded && result.OperationKind == RealtimeWorldInteractionOperationKind.Open)
        {
            return;
        }

        body.OnInteractionResult(result);
    }

    public void OnGatheringState(RealtimeGatheringState state)
    {
        body.OnGathering(state);
    }

    private HeadlessSimulationBotClient CreateClient(StressBotAdmission admission, SimulationBotWorldView world)
    {
        return new HeadlessSimulationBotClient(
            new SimulationBotConnectionOptions(admission.WorkerHost, admission.WorkerUdpPort, options.JoinTimeout),
            new SimulationBotIdentity(
                admission.BotIndex,
                admission.AccountId,
                admission.CharacterId,
                admission.CharacterName,
                admission.Ticket,
                admission.ShardId,
                admission.WorldId),
            this,
            recordInputAcknowledgement,
            this,
            world);
    }

    private PlayingBotBody CreateBody(HeadlessSimulationBotClient connection, SimulationBotWorldView world)
    {
        return new PlayingBotBody(
            botIndex,
            assignment.Role,
            connection,
            world,
            kit,
            context.GameData,
            context.Navigation,
            gameplayMetrics,
            context.Metrics,
            options.GameplayOperationTimeout,
            refreshInventory);
    }

    // Nothing in here may throw: an exception on a poll thread ends the poll thread. A failed
    // churn fails the bot with the reason instead.
    private void AdvanceChurn(long now, CancellationToken cancellationToken)
    {
        switch (phase)
        {
            case PlayingChurnPhase.Waiting:
                Wait(now);
                break;
            case PlayingChurnPhase.Retreating:
                Retreat(now);
                break;
            case PlayingChurnPhase.Settling:
                Settle(now);
                break;
            case PlayingChurnPhase.Leaving:
                AwaitLeave(now, cancellationToken);
                break;
            case PlayingChurnPhase.Away:
                AwaitWayBack(now);
                break;
            case PlayingChurnPhase.Rejoining:
                AwaitRejoin(now, cancellationToken);
                break;
        }
    }

    private void Wait(long now)
    {
        if (draining)
        {
            Skip(PlayingCounter.ChurnSkippedLate, now);
            return;
        }

        if (now < nextChurnCheckTimestamp)
        {
            return;
        }

        nextChurnCheckTimestamp = now + Ticks(ChurnCheckInterval);
        if (Stopwatch.GetElapsedTime(churnRequestedTimestamp, now) > ChurnSafetyWait)
        {
            Skip(PlayingCounter.ChurnSkippedUnsafe, now);
            return;
        }

        if (client.State != SimulationBotClientState.Joined)
        {
            return;
        }

        switch (PlayingLeaveSafety.Evaluate(body, role.IsSetUp))
        {
            case PlayingLeaveReadiness.Ready:
                roleSuspended = true;
                body.Stand();
                settledSinceTimestamp = 0;
                Enter(PlayingChurnPhase.Settling, now);
                break;
            case PlayingLeaveReadiness.TooCloseToMobs
                when retreats < MaximumRetreats && PlayingLeaveSafety.RetreatPoint(body) is { } point:
                retreats++;
                roleSuspended = true;
                context.Metrics.Count(PlayingCounter.ChurnRetreats);
                body.WalkTo(point);
                Enter(PlayingChurnPhase.Retreating, now);
                break;
        }
    }

    private void Retreat(long now)
    {
        if (draining || Stopwatch.GetElapsedTime(churnRequestedTimestamp, now) > ChurnSafetyWait)
        {
            Skip(draining ? PlayingCounter.ChurnSkippedLate : PlayingCounter.ChurnSkippedUnsafe, now);
            return;
        }

        if (now < nextChurnCheckTimestamp)
        {
            return;
        }

        nextChurnCheckTimestamp = now + Ticks(ChurnCheckInterval);
        var readiness = PlayingLeaveSafety.Evaluate(body, role.IsSetUp);
        if (readiness == PlayingLeaveReadiness.Busy)
        {
            // Something found the bot on its way: the role deals with it.
            ResumeRole(now);
            Enter(PlayingChurnPhase.Waiting, now);
            return;
        }

        if (body.MoveState is PlayingMoveState.Planning or PlayingMoveState.Walking
            && Stopwatch.GetElapsedTime(phaseTimestamp, now) < ChurnRetreatLimit)
        {
            return;
        }

        if (readiness == PlayingLeaveReadiness.Ready)
        {
            body.Stand();
            settledSinceTimestamp = 0;
            Enter(PlayingChurnPhase.Settling, now);
            return;
        }

        ResumeRole(now);
        Enter(PlayingChurnPhase.Waiting, now);
    }

    private void Settle(long now)
    {
        if (draining)
        {
            Skip(PlayingCounter.ChurnSkippedLate, now);
            return;
        }

        if (now < nextChurnCheckTimestamp)
        {
            return;
        }

        nextChurnCheckTimestamp = now + Ticks(ChurnCheckInterval);
        if (Stopwatch.GetElapsedTime(churnRequestedTimestamp, now) > ChurnSafetyWait)
        {
            Skip(PlayingCounter.ChurnSkippedUnsafe, now);
            return;
        }

        if (PlayingLeaveSafety.Evaluate(body, role.IsSetUp) != PlayingLeaveReadiness.Ready)
        {
            ResumeRole(now);
            Enter(PlayingChurnPhase.Waiting, now);
            return;
        }

        if (!PlayingLeaveSafety.IsStanding(body))
        {
            settledSinceTimestamp = 0;
            return;
        }

        if (settledSinceTimestamp == 0)
        {
            settledSinceTimestamp = now;
            return;
        }

        if (Stopwatch.GetElapsedTime(settledSinceTimestamp, now) < ChurnSettleTime
            || body.View.OwnCombat is not { } own
            || body.View.Session is not { } session)
        {
            return;
        }

        leave = new PlayingLeaveSnapshot(
            body.Position,
            own.Health,
            own.Mana,
            own.MaximumHealth,
            DateTime.UtcNow,
            session.SimulationSessionId,
            0);
        gameplayMetrics.RecordRequest(PlayingOperations.Leave);
        leaveSentTimestamp = now;
        client.BeginLeave(now);
        Enter(PlayingChurnPhase.Leaving, now);
    }

    private void AwaitLeave(long now, CancellationToken cancellationToken)
    {
        switch (client.State)
        {
            case SimulationBotClientState.Completed:
                break;
            case SimulationBotClientState.Failed:
                var failed = client.Capture();
                gameplayMetrics.RecordResult(
                    PlayingOperations.Leave,
                    Milliseconds(leaveSentTimestamp, now),
                    false,
                    failed.FailureCode,
                    expectedRejection: false);
                Fail(failed.FailureCode ?? "leave_failed", failed.FailureMessage ?? "The churn's leave failed.");
                return;
            default:
                return;
        }

        gameplayMetrics.RecordResult(PlayingOperations.Leave, Milliseconds(leaveSentTimestamp, now), true, null, expectedRejection: false);
        var bodySeconds = client.Capture().BodySeconds;
        leave = leave! with { LeftAt = DateTime.UtcNow, BodySeconds = bodySeconds };
        var metrics = context.Metrics;
        metrics.Count(PlayingCounter.Leaves);
        metrics.Count(assignment.Role switch
        {
            PlayingRole.Fighter => PlayingCounter.LeavesFighter,
            PlayingRole.Gatherer => PlayingCounter.LeavesGatherer,
            PlayingRole.Trader => PlayingCounter.LeavesTrader,
            _ => PlayingCounter.LeavesWalker
        });
        if (churn!.Logout)
        {
            metrics.Count(PlayingCounter.LeavesWithLogout);
        }

        RetireClient(now);
        var delay = churn.Kind == PlayingChurnKind.Body
            ? churn.Delay
            : TimeSpan.FromSeconds(bodySeconds) + churn.Delay;
        rejoinPlan = new PlayingRejoinPlan(botIndex, churn.Logout, now + Ticks(delay), leave.LeftAt, leave.SimulationSessionId);
        try
        {
            wayBack = context.Rejoin!(rejoinPlan, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            wayBack = Task.FromException<PlayingRejoinTicket>(exception);
        }

        Enter(PlayingChurnPhase.Away, now);
    }

    private void AwaitWayBack(long now)
    {
        if (wayBack is not { IsCompleted: true } completed)
        {
            return;
        }

        wayBack = null;
        var intended = churn!.Kind == PlayingChurnKind.Body ? PlayingOperations.RejoinBody : PlayingOperations.RejoinSaved;
        if (completed.IsCanceled)
        {
            // The run is being cancelled; the bot stays out of the game.
            Fail("run_cancelled", "The run was cancelled while the bot was out of the game.");
            return;
        }

        if (completed.IsFaulted)
        {
            var exception = completed.Exception!.GetBaseException();
            var code = exception is FullStackApiException api ? api.Code : "rejoin_failed";
            gameplayMetrics.RecordRequest(intended);
            gameplayMetrics.RecordResult(intended, 0, false, code, expectedRejection: false);
            Fail(code, $"The way back into the game failed: {exception.Message}");
            return;
        }

        var ticket = completed.Result;
        rejoinTicket = ticket;
        var world = new SimulationBotWorldView(ticket.Admission.CharacterName);
        var connection = CreateClient(ticket.Admission, world);
        body = CreateBody(connection, world);
        Volatile.Write(ref client, connection);
        clientRetired = false;
        connection.Start();
        if (cadenceOpen && !cadenceEnded)
        {
            connection.BeginRemoteCadenceWindow(now);
        }

        joinedTimestamp = 0;
        rejoinFound = null;
        Enter(PlayingChurnPhase.Rejoining, now);
    }

    private sealed record RejoinFound(
        int Health,
        int MaximumHealth,
        RealtimeCombatLifeState LifeState,
        DateTime ObservedAt);

    private void AwaitRejoin(long now, CancellationToken cancellationToken)
    {
        var ticket = rejoinTicket!;
        var reconnect = body.View.Session?.IsReconnect ?? ticket.IsReconnect;
        var operation = reconnect ? PlayingOperations.RejoinBody : PlayingOperations.RejoinSaved;
        if (client.State == SimulationBotClientState.Failed)
        {
            var failed = client.Capture();
            gameplayMetrics.RecordRequest(operation);
            gameplayMetrics.RecordResult(operation, Milliseconds(ticket.WayBackStartedTimestamp, now), false, failed.FailureCode, expectedRejection: false);
            Fail(failed.FailureCode ?? "rejoin_failed", failed.FailureMessage ?? "The join of the way back failed.");
            return;
        }

        if (client.State != SimulationBotClientState.Joined)
        {
            return;
        }

        if (joinedTimestamp == 0)
        {
            joinedTimestamp = now;
        }

        if (body.View.OwnCombat is not { } latest || body.View.Session is not { } session)
        {
            if (Stopwatch.GetElapsedTime(joinedTimestamp, now) > ChurnOwnerStateWait)
            {
                gameplayMetrics.RecordRequest(operation);
                gameplayMetrics.RecordResult(operation, Milliseconds(ticket.WayBackStartedTimestamp, joinedTimestamp), false, "rejoin_no_owner_state", expectedRejection: false);
                Fail("rejoin_no_owner_state", "The worker sent no owner combat state after the join of the way back.");
            }

            return;
        }

        // The check judges what the bot found when it came back. Reading the saved state below
        // can take longer than the health regenerates, so the first observation is kept.
        rejoinFound ??= new RejoinFound(latest.Health, latest.MaximumHealth, latest.LifeState, DateTime.UtcNow);
        var own = rejoinFound;

        // The ticket named the body, but its release came first, so the join entered from the
        // saved place: the database says what the body left behind.
        var facts = ticket.Facts;
        if (!reconnect && facts is null)
        {
            if (lateFacts is null)
            {
                try
                {
                    lateFacts = context.ReadRejoinFacts!(rejoinPlan!, cancellationToken);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    lateFacts = Task.FromException<PlayingRejoinFacts>(exception);
                }
            }

            if (!lateFacts.IsCompleted)
            {
                return;
            }

            var read = lateFacts;
            lateFacts = null;
            if (!read.IsCompletedSuccessfully)
            {
                var reason = read.IsCanceled ? "cancelled" : read.Exception!.GetBaseException().Message;
                gameplayMetrics.RecordRequest(operation);
                gameplayMetrics.RecordResult(operation, Milliseconds(ticket.WayBackStartedTimestamp, joinedTimestamp), false, "rejoin_facts_failed", expectedRejection: false);
                Fail("rejoin_facts_failed", $"The saved state of the body could not be read: {reason}");
                return;
            }

            facts = read.Result;
        }

        var start = session.InitialPlayerState;
        var verdict = PlayingRejoinCheck.Judge(
            leave!,
            new PlayingRejoinObservation(
                reconnect,
                new SimulationVector3(start.PositionX, start.PositionY, start.PositionZ),
                own.Health,
                own.MaximumHealth,
                own.LifeState,
                own.ObservedAt),
            facts,
            context.Regeneration.HealthPerSecond);
        var metrics = context.Metrics;
        metrics.Count(reconnect ? PlayingCounter.RejoinsBody : PlayingCounter.RejoinsSaved);
        if (reconnect && churn!.Kind == PlayingChurnKind.Saved)
        {
            metrics.Count(PlayingCounter.RejoinsSavedFoundBody);
        }
        else if (!reconnect && churn!.Kind == PlayingChurnKind.Body)
        {
            metrics.Count(PlayingCounter.RejoinsBodyFoundGone);
        }

        if (verdict.BodyWasHit)
        {
            metrics.Count(PlayingCounter.RejoinBodiesHit);
        }

        if (verdict.BodyDied)
        {
            metrics.Count(PlayingCounter.RejoinBodiesDied);
        }

        gameplayMetrics.RecordRequest(operation);
        gameplayMetrics.RecordResult(
            operation,
            Milliseconds(ticket.WayBackStartedTimestamp, joinedTimestamp),
            verdict.Passed,
            verdict.Code,
            expectedRejection: false);
        if (!verdict.Passed)
        {
            metrics.RecordRejoinMismatch(new PlayingRejoinMismatch(botIndex, operation, verdict.Code!, verdict.Detail));
            Console.WriteLine($"Bot {botIndex} ({characterName}) {operation} failed its check with {verdict.Code}: {verdict.Detail}.");
        }

        churn = null;
        leave = null;
        rejoinPlan = null;
        rejoinTicket = null;
        ResumeRole(now);
        Enter(PlayingChurnPhase.None, now);
    }

    private void Skip(PlayingCounter reason, long now)
    {
        context.Metrics.Count(reason);
        churn = null;
        if (roleSuspended)
        {
            body.Stand();
            ResumeRole(now);
        }

        Enter(PlayingChurnPhase.None, now);
    }

    private void ResumeRole(long now)
    {
        roleSuspended = false;
        role.Resume(RoleTime(now));
        nextThinkTimestamp = now;
    }

    private void Fail(string code, string message)
    {
        failureCode = code;
        failureMessage = message;
        roleSuspended = false;
        Enter(PlayingChurnPhase.None, 0);
    }

    // The connection that left: its numbers stay with the bot, and its socket closes.
    private void RetireClient(long now)
    {
        if (cadenceOpen && !cadenceEnded)
        {
            client.EndRemoteCadenceWindow(now);
        }

        var snapshot = client.Capture();
        retiredPacketsSent += snapshot.PacketsSent;
        retiredBytesSent += snapshot.BytesSent;
        retiredPacketsReceived += snapshot.PacketsReceived;
        retiredBytesReceived += snapshot.BytesReceived;
        Interlocked.Add(ref retiredSnapshots, snapshot.SnapshotsReceived);
        Interlocked.Add(ref retiredMissingSnapshots, snapshot.EstimatedMissingSnapshots);
        retiredSpawnPackets += snapshot.SpawnPackets;
        retiredDespawnPackets += snapshot.DespawnPackets;
        retiredInputsDropped += snapshot.UnacknowledgedInputsDropped;
        retiredLatestTick = Math.Max(retiredLatestTick, snapshot.LatestServerTick);
        firstJoinLatencyMs ??= snapshot.JoinLatencyMs;
        retiredCadence = SimulationBotRemoteCadenceSummary.Combine(retiredCadence, snapshot.RemoteCadence);
        clientRetired = true;
        client.Dispose();
    }

    private void Enter(PlayingChurnPhase next, long now)
    {
        phase = next;
        phaseTimestamp = now;
    }

    private TimeSpan RoleTime(long now)
    {
        return Stopwatch.GetElapsedTime(createdTimestamp, now);
    }

    private static double Milliseconds(long start, long end)
    {
        return Math.Max(0d, (end - start) * 1000d / Stopwatch.Frequency);
    }

    private static long Ticks(TimeSpan duration)
    {
        return (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }
}
