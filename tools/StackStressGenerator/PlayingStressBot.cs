using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using Mmorpg.WorldData.Items;
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
}

// What the playing bots of one run share: their assignments, the item catalog they read like a
// client, the navigation, and the metrics.
public sealed record PlayingRunContext(
    PlayingScenario Scenario,
    IReadOnlyDictionary<int, PlayingAssignment> Assignments,
    ItemCatalogRuntimeDocument ItemCatalog,
    SimulationBotNavigation? Navigation,
    PlayingMetrics Metrics);

// One bot of the playing workload: a headless client with a world view, the body that sends
// its intents like a Unity client and measures them, and the state machine of its role, which
// thinks every few hundred milliseconds.
public sealed class PlayingStressBot : IStressBot, ISimulationBotRealtimeObserver, ISimulationBotInputSource
{
    // A decision follows the previous one after this long, drawn per bot from the seed.
    public static readonly TimeSpan MinimumThinkDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumThinkDelay = TimeSpan.FromMilliseconds(650);

    private readonly HeadlessSimulationBotClient client;
    private readonly PlayingBotBody body;
    private readonly IPlayingRole role;
    private readonly Random thinkRandom;
    private readonly long createdTimestamp = Stopwatch.GetTimestamp();
    private long nextThinkTimestamp;
    private bool draining;

    public PlayingStressBot(
        StressGeneratorOptions options,
        StressBotAdmission admission,
        PlayingRunContext context,
        StressLatencyAccumulator inputAcknowledgementLatencies,
        StressLatencyAccumulator intervalInputAcknowledgementLatencies,
        StressGameplayMetrics gameplayMetrics,
        Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(context);
        var assignment = context.Assignments[admission.BotIndex];
        var world = new SimulationBotWorldView(admission.CharacterName);
        if (admission.Inventory is { } inventory)
        {
            world.Items.Load(PlayingBotBody.ToItemSnapshot(inventory));
        }

        client = new HeadlessSimulationBotClient(
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
            latencyMilliseconds =>
            {
                inputAcknowledgementLatencies.Record(latencyMilliseconds);
                intervalInputAcknowledgementLatencies.Record(latencyMilliseconds);
            },
            this,
            world);
        // The kit is what the kit fixture granted, as the bot's inventory shows it.
        var kit = PlayingRoster.ResolveKit(
            assignment.Kit,
            world.Items.Items.Select(item => item.DefinitionId),
            context.ItemCatalog);
        body = new PlayingBotBody(
            admission.BotIndex,
            client,
            world,
            kit,
            context.Navigation,
            gameplayMetrics,
            context.Metrics,
            options.GameplayOperationTimeout,
            refreshInventory);
        var decisions = new Random(assignment.DecisionSeed);
        thinkRandom = new Random(PlayingRoster.Mix(assignment.DecisionSeed, 0x5448494e));
        role = assignment.Role switch
        {
            PlayingRole.Fighter => new FighterRole(
                assignment.Home ?? throw new InvalidOperationException($"Fighter bot {admission.BotIndex} has no hunting ground."),
                context.Scenario.TeleportToGrounds,
                decisions),
            PlayingRole.Walker => new WalkerRole(assignment.Home, context.Scenario.TeleportToGrounds, decisions),
            _ => throw new InvalidOperationException($"The role {assignment.Role} is not implemented.")
        };
    }

    public StressBotState State => (StressBotState)client.State;

    public bool IsGameplayIdle => body.IsIdle;

    public IPlayingRole Role => role;

    public void Start()
    {
        client.Start();
    }

    public void Poll(long nowTimestamp, CancellationToken cancellationToken)
    {
        client.Poll(nowTimestamp);
        body.Update(nowTimestamp, cancellationToken);
        if (draining
            || client.State != SimulationBotClientState.Joined
            || nowTimestamp < nextThinkTimestamp)
        {
            return;
        }

        role.Think(body, Stopwatch.GetElapsedTime(createdTimestamp, nowTimestamp));
        body.AfterThink();
        var delay = MinimumThinkDelay
            + TimeSpan.FromTicks((long)((MaximumThinkDelay - MinimumThinkDelay).Ticks * thinkRandom.NextDouble()));
        nextThinkTimestamp = nowTimestamp + (long)Math.Ceiling(delay.TotalSeconds * Stopwatch.Frequency);
    }

    public void BeginLeave(long nowTimestamp)
    {
        client.BeginLeave(nowTimestamp);
    }

    public void BeginGameplayDrain()
    {
        draining = true;
        body.Stand();
    }

    public void BeginRemoteCadenceWindow(long nowTimestamp)
    {
        client.BeginRemoteCadenceWindow(nowTimestamp);
    }

    public void EndRemoteCadenceWindow(long nowTimestamp)
    {
        client.EndRemoteCadenceWindow(nowTimestamp);
    }

    public StressBotSnapshot Capture()
    {
        var snapshot = client.Capture();
        return new StressBotSnapshot(
            snapshot.BotIndex,
            snapshot.CharacterName,
            (StressBotState)snapshot.State,
            snapshot.JoinLatencyMs,
            snapshot.PacketsSent,
            snapshot.BytesSent,
            snapshot.PacketsReceived,
            snapshot.BytesReceived,
            snapshot.SnapshotsReceived,
            snapshot.EstimatedMissingSnapshots,
            snapshot.SpawnPackets,
            snapshot.DespawnPackets,
            snapshot.UnacknowledgedInputsDropped,
            snapshot.LatestServerTick,
            snapshot.FailureCode,
            snapshot.FailureMessage,
            snapshot.RemoteCadence);
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
}

// The real body of a playing bot: it sends one intent at a time over UDP, records the latency
// and result code of every operation, classifies rejections with PlayingExpectedConflicts,
// plans walks on the shared planner, follows them like the Unity client, reads the full item
// state over HTTP when a committed change reached the bot only as a newer carry state, and
// counts what its combat events say.
public sealed class PlayingBotBody : IPlayingBotBody
{
    // The worker reports a blocked path with its blocked ticks; this many is one second.
    private const int BlockedTicksLimit = 30;

    // A newer carry state waits this long for the change that explains it before a reload.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromSeconds(1);

    private readonly HeadlessSimulationBotClient client;
    private readonly SimulationBotNavigation? navigation;
    private readonly StressGameplayMetrics operations;
    private readonly PlayingMetrics metrics;
    private readonly TimeSpan operationTimeout;
    private readonly Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory;
    private readonly List<RealtimeCombatEvent> recentEvents = [];
    private readonly List<RealtimeCombatEvent> drained = [];
    private PendingIntent? pending;
    private PlayingIntentOutcome? outcome;
    private Task<FullStackCharacterInventoryResponse?>? reload;
    private bool reloadRequested;
    private long behindSinceTimestamp;
    private Task<MovementPathPlan>? plan;
    private SimulationVector3 planDestination;
    private long walkDeadlineTimestamp;
    private uint lastPathId;
    private PlayingGroundProbe? probe;
    private ulong firstHitTarget;
    private long firstHitStartedTimestamp;
    private long nowTimestamp;

    public PlayingBotBody(
        int botIndex,
        HeadlessSimulationBotClient client,
        SimulationBotWorldView view,
        PlayingKit kit,
        SimulationBotNavigation? navigation,
        StressGameplayMetrics operations,
        PlayingMetrics metrics,
        TimeSpan operationTimeout,
        Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory)
    {
        BotIndex = botIndex;
        this.client = client;
        View = view;
        Kit = kit;
        this.navigation = navigation;
        this.operations = operations;
        this.metrics = metrics;
        this.operationTimeout = operationTimeout;
        this.refreshInventory = refreshInventory;
    }

    public int BotIndex { get; }

    public SimulationBotWorldView View { get; }

    public PlayingKit Kit { get; }

    public bool IsReady => client.State == SimulationBotClientState.Joined
        && View.Items.HasState
        && !View.Items.IsBehindServer
        && reload is null;

    public bool HasPendingIntent => pending is not null;

    public bool IsIdle => pending is null && reload is null;

    public IReadOnlyList<RealtimeCombatEvent> RecentCombatEvents => recentEvents;

    public PlayingMoveState MoveState { get; private set; } = PlayingMoveState.Standing;

    public bool RouteReachedDestination { get; private set; }

    public SimulationVector3 Position => new(View.OwnPosition.X, View.OwnPosition.Y, View.OwnPosition.Z);

    public RealtimeMovementPath? CurrentPath { get; private set; }

    public static SimulationBotItemSnapshot ToItemSnapshot(FullStackCharacterInventoryResponse inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var containers = new List<SimulationBotItemContainer>
        {
            new(
                inventory.PermanentInventory.ContainerId,
                inventory.PermanentInventory.Revision,
                inventory.PermanentInventory.SlotCapacity ?? inventory.PermanentInventory.Slots.Count)
        };
        var items = new List<SimulationBotOwnedItem>();
        AddContainerItems(inventory.PermanentInventory, items);
        if (inventory.EquippedBag is { } bag)
        {
            containers.Add(new SimulationBotItemContainer(
                bag.Contents.ContainerId,
                bag.Contents.Revision,
                bag.Contents.SlotCapacity ?? bag.Contents.Slots.Count));
            AddContainerItems(bag.Contents, items);
        }

        foreach (var slot in inventory.Equipment)
        {
            if (slot.Item is { } item)
            {
                items.Add(new SimulationBotOwnedItem(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    item.Quantity,
                    item.Revision,
                    Guid.Empty,
                    -1,
                    slot.EquipmentSlotId));
            }
        }

        return new SimulationBotItemSnapshot(
            inventory.ItemStateRevision,
            inventory.PermanentInventory.ContainerId,
            containers,
            items);
    }

    public PlayingIntentOutcome? TakeOutcome()
    {
        var taken = outcome;
        outcome = null;
        return taken;
    }

    public bool TryFindGround(float x, float z, out SimulationVector3 ground)
    {
        ground = default;
        if (navigation is null || View.Session is not { } session)
        {
            return false;
        }

        probe ??= new PlayingGroundProbe(
            navigation.CollisionWorld,
            SimulationBotNavigation.ToSimulationSettings(session.MovementSettings));
        return probe.TryFindGround(x, View.OwnPosition.Y, z, out ground);
    }

    public bool Equip(SimulationBotOwnedItem item, string equipmentSlotId)
    {
        ArgumentNullException.ThrowIfNull(item);
        var intent = RealtimeItemOperationIntent.CreateEquip(
            Guid.NewGuid(),
            View.Items.Revision,
            item.ItemInstanceId,
            item.Revision,
            equipmentSlotId);
        return Send(PlayingOperations.Equip, intent.OperationId, () => client.SendItemOperation(intent));
    }

    public bool Teleport(SimulationVector3 destination)
    {
        var intent = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), destination.X, destination.Y, destination.Z);
        return Send(PlayingOperations.Teleport, intent.OperationId, () => client.SendAdminIntent(intent));
    }

    public bool Attack(ulong targetEntityId)
    {
        var intent = RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId);
        if (!Send(PlayingOperations.Attack, intent.OperationId, () => client.SendCombatIntent(intent)))
        {
            return false;
        }

        firstHitTarget = targetEntityId;
        firstHitStartedTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    public bool ClearTarget()
    {
        var intent = RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid());
        firstHitTarget = 0;
        return Send(PlayingOperations.ClearTarget, intent.OperationId, () => client.SendCombatIntent(intent));
    }

    public bool OpenCorpse(Guid corpseId)
    {
        var intent = RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId);
        return Send(PlayingOperations.LootOpen, intent.OperationId, () => client.SendCorpseInteraction(intent));
    }

    public bool LootAll(Guid corpseId, long expectedRevision)
    {
        var intent = RealtimeCorpseInteractionIntent.CreateLootAll(Guid.NewGuid(), corpseId, expectedRevision);
        return Send(PlayingOperations.LootAll, intent.OperationId, () => client.SendCorpseInteraction(intent));
    }

    public bool CloseCorpse(Guid corpseId, long expectedRevision)
    {
        var intent = RealtimeCorpseInteractionIntent.CreateClose(Guid.NewGuid(), corpseId, expectedRevision);
        return Send(PlayingOperations.LootClose, intent.OperationId, () => client.SendCorpseInteraction(intent));
    }

    public void WalkTo(SimulationVector3 destination)
    {
        if (View.Session is not { } session)
        {
            MoveState = PlayingMoveState.Failed;
            return;
        }

        CurrentPath = null;
        plan = null;
        RouteReachedDestination = false;
        planDestination = destination;
        if (navigation is null
            || !string.Equals(navigation.CollisionWorld.Revision, session.CollisionRevision, StringComparison.Ordinal))
        {
            // Without the worker's collision a walk is a straight line its collision stops.
            StartPath([new RealtimeMovementPathWaypoint(destination.X, destination.Y, destination.Z)], true);
            return;
        }

        metrics.Count(PlayingCounter.RoutePlans);
        plan = navigation.TryPlan(session.MovementSettings, Position, destination);
        if (plan is null)
        {
            metrics.Count(PlayingCounter.RoutePlansBusy);
            MoveState = PlayingMoveState.Failed;
            return;
        }

        MoveState = PlayingMoveState.Planning;
    }

    public void Stand()
    {
        CurrentPath = null;
        plan = null;
        if (MoveState is PlayingMoveState.Planning or PlayingMoveState.Walking)
        {
            MoveState = PlayingMoveState.Standing;
        }
    }

    public void Count(PlayingCounter counter, long amount = 1)
    {
        metrics.Count(counter, amount);
    }

    public void OnJoined(RealtimeJoinAccepted session)
    {
        nowTimestamp = Stopwatch.GetTimestamp();
    }

    // Matches a result to the pending intent and records the operation. Like the Unity client,
    // a rejection that asks for it reads the full item state again, and a committed result
    // does when the change it reports did not reach the bot's items.
    public void Complete(
        Guid operationId,
        bool succeeded,
        string? code,
        bool requiresInventoryRefresh,
        long? itemStateRevision)
    {
        if (itemStateRevision is { } revision)
        {
            View.Items.ObserveServerRevision(revision);
            if (succeeded && revision > View.Items.Revision)
            {
                reloadRequested = true;
            }
        }

        if (!succeeded && requiresInventoryRefresh)
        {
            reloadRequested = true;
        }

        if (pending is null || pending.OperationId != operationId)
        {
            operations.RecordRequest("unexpected_result");
            operations.RecordResult("unexpected_result", 0, false, "unexpected_result", expectedRejection: false);
            return;
        }

        var expected = !succeeded && PlayingExpectedConflicts.IsExpected(pending.Operation, code);
        operations.RecordResult(
            pending.Operation,
            Stopwatch.GetElapsedTime(pending.SentTimestamp).TotalMilliseconds,
            succeeded,
            succeeded ? null : code,
            expected);
        if (!succeeded && pending.Operation == PlayingOperations.Attack)
        {
            firstHitTarget = 0;
        }

        outcome = new PlayingIntentOutcome(pending.Operation, succeeded, succeeded ? null : code, expected);
        pending = null;
    }

    public void Update(long now, CancellationToken cancellationToken)
    {
        nowTimestamp = now;
        if (pending is not null && Stopwatch.GetElapsedTime(pending.SentTimestamp, now) >= operationTimeout)
        {
            operations.RecordTimeout(pending.Operation);
            outcome = new PlayingIntentOutcome(pending.Operation, false, "operation_timeout", false);
            pending = null;
            reloadRequested = true;
        }

        CompleteReload();
        if (client.State == SimulationBotClientState.Joined)
        {
            StartReloadWhenBehind(now, cancellationToken);
            FollowRoute(now);
            DrainCombatEvents(now);
        }
    }

    // The role saw the events; the next think sees only newer ones.
    public void AfterThink()
    {
        recentEvents.Clear();
    }

    private bool Send(string operation, Guid operationId, Func<bool> send)
    {
        if (pending is not null || client.State != SimulationBotClientState.Joined || !send())
        {
            return false;
        }

        operations.RecordRequest(operation);
        pending = new PendingIntent(operation, operationId, Stopwatch.GetTimestamp());
        outcome = null;
        return true;
    }

    private void StartReloadWhenBehind(long now, CancellationToken cancellationToken)
    {
        // Like the Unity client, a bot without a full item state reads one after its join.
        if (!View.Items.HasState)
        {
            reloadRequested = true;
        }

        if (!View.Items.IsBehindServer && !reloadRequested)
        {
            behindSinceTimestamp = 0;
            return;
        }

        if (behindSinceTimestamp == 0)
        {
            behindSinceTimestamp = now;
        }

        // The change that explains a newer revision may still be on its way, and an intent in
        // flight may still patch it, so a reload waits for both.
        if (reload is not null
            || pending is not null
            || (!reloadRequested && Stopwatch.GetElapsedTime(behindSinceTimestamp, now) < ReloadDelay))
        {
            return;
        }

        reloadRequested = false;
        metrics.Count(PlayingCounter.ItemStateReloads);
        reload = refreshInventory(cancellationToken);
    }

    private void CompleteReload()
    {
        if (reload is null || !reload.IsCompleted)
        {
            return;
        }

        try
        {
            var snapshot = reload.GetAwaiter().GetResult();
            if (snapshot is null)
            {
                operations.RecordInventoryRefreshFailure();
            }
            else
            {
                View.Items.Load(ToItemSnapshot(snapshot));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operations.RecordInventoryRefreshFailure();
            Console.WriteLine($"Bot {BotIndex} item state reload failed: {exception.Message}");
        }
        finally
        {
            reload = null;
            behindSinceTimestamp = 0;
        }
    }

    private void FollowRoute(long now)
    {
        if (View.OwnCombat?.LifeState == RealtimeCombatLifeState.Dead
            && MoveState is PlayingMoveState.Planning or PlayingMoveState.Walking)
        {
            // The worker halted the dead character; its route must not run after the respawn.
            Stand();
            MoveState = PlayingMoveState.Failed;
            return;
        }

        if (plan is { IsCompleted: true } completed)
        {
            plan = null;
            var result = completed.IsCompletedSuccessfully ? completed.Result : null;
            if (result is null || result.Status == MovementPathPlanStatus.NoPath || result.Waypoints.Count == 0)
            {
                metrics.Count(PlayingCounter.RoutePlansWithoutPath);
                MoveState = PlayingMoveState.Failed;
                return;
            }

            var waypoints = new RealtimeMovementPathWaypoint[result.Waypoints.Count];
            for (var index = 0; index < waypoints.Length; index++)
            {
                var waypoint = result.Waypoints[index];
                waypoints[index] = new RealtimeMovementPathWaypoint(waypoint.X, waypoint.Y, waypoint.Z);
            }

            StartPath(waypoints, result.Status == MovementPathPlanStatus.Reached);
            return;
        }

        if (MoveState != PlayingMoveState.Walking || CurrentPath is null)
        {
            return;
        }

        var progress = View.OwnPathProgress;
        if (progress is not null && progress.PathId == CurrentPath.PathId)
        {
            if (progress.NextWaypointIndex >= CurrentPath.Waypoints.Length)
            {
                CurrentPath = null;
                MoveState = PlayingMoveState.Arrived;
                return;
            }

            if (progress.BlockedTicks >= BlockedTicksLimit)
            {
                metrics.Count(PlayingCounter.WalksFailed);
                CurrentPath = null;
                MoveState = PlayingMoveState.Failed;
                return;
            }
        }

        if (now >= walkDeadlineTimestamp)
        {
            metrics.Count(PlayingCounter.WalksFailed);
            CurrentPath = null;
            MoveState = PlayingMoveState.Failed;
        }
    }

    private void StartPath(RealtimeMovementPathWaypoint[] waypoints, bool reachesDestination)
    {
        unchecked
        {
            lastPathId++;
        }

        if (lastPathId == 0)
        {
            lastPathId = 1;
        }

        var length = 0f;
        var previous = Position;
        foreach (var waypoint in waypoints)
        {
            var dx = waypoint.X - previous.X;
            var dz = waypoint.Z - previous.Z;
            length += MathF.Sqrt((dx * dx) + (dz * dz));
            previous = new SimulationVector3(waypoint.X, waypoint.Y, waypoint.Z);
        }

        var speed = Math.Max(0.5f, View.Session?.MovementSettings.WalkSpeed ?? 5f);
        CurrentPath = new RealtimeMovementPath(lastPathId, waypoints);
        RouteReachedDestination = reachesDestination;
        MoveState = PlayingMoveState.Walking;
        walkDeadlineTimestamp = nowTimestamp
            + (long)Math.Ceiling(((length / speed * 2d) + 3d) * Stopwatch.Frequency);
    }

    private void DrainCombatEvents(long now)
    {
        drained.Clear();
        View.DrainCombatEvents(drained);
        if (drained.Count == 0)
        {
            return;
        }

        var own = View.OwnEntityId;
        foreach (var combatEvent in drained)
        {
            switch (combatEvent.Kind)
            {
                case RealtimeCombatEventKind.Hit:
                    if (combatEvent.SourceEntityId == own
                        && firstHitTarget != 0
                        && combatEvent.TargetEntityId == firstHitTarget)
                    {
                        metrics.RecordTiming(
                            PlayingMetrics.AttackFirstHit,
                            Stopwatch.GetElapsedTime(firstHitStartedTimestamp, now).TotalMilliseconds);
                        firstHitTarget = 0;
                    }

                    if (combatEvent.TargetEntityId == own
                        && View.Actors.TryGetValue(combatEvent.SourceEntityId, out var attacker)
                        && attacker.Kind == RealtimeWorldActorKind.Mob)
                    {
                        metrics.Count(PlayingCounter.MobDamageTaken, combatEvent.Damage);
                    }

                    break;
                case RealtimeCombatEventKind.Died:
                    if (combatEvent.TargetEntityId == own)
                    {
                        metrics.Count(PlayingCounter.BotDeaths);
                        firstHitTarget = 0;
                    }
                    else if (combatEvent.SourceEntityId == own)
                    {
                        // Bots attack only Mobs, and a Mob's despawn can follow its death in
                        // the same poll, so the killer alone counts the kill.
                        metrics.Count(PlayingCounter.Kills);
                    }

                    if (combatEvent.TargetEntityId == firstHitTarget)
                    {
                        firstHitTarget = 0;
                    }

                    break;
            }
        }

        recentEvents.AddRange(drained);
    }

    private static void AddContainerItems(
        FullStackItemContainerResponse container,
        ICollection<SimulationBotOwnedItem> items)
    {
        foreach (var slot in container.Slots)
        {
            if (slot.Item is { } item)
            {
                items.Add(new SimulationBotOwnedItem(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    item.Quantity,
                    item.Revision,
                    container.ContainerId,
                    slot.SlotIndex,
                    string.Empty));
            }
        }
    }

    private sealed record PendingIntent(string Operation, Guid OperationId, long SentTimestamp);
}
