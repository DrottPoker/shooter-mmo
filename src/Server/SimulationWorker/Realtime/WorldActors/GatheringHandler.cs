using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs gathering at resource nodes. A validated StartGathering action starts one activity
// per peer; every due cycle reserves a node charge and asks GameService for the yield
// behind the peer's other authority work. A committed yield consumes the charge, the last
// charge depletes the node for everyone, and the central timer queue respawns it. Moving,
// closing the interaction, or a rejected yield stops gathering. Loop-confined.
internal sealed class GatheringHandler(
    SimulationWorkerConfig config,
    RealtimeSimulationClock clock,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    WorldInteractionLeaseRegistry? leases,
    WorldActorTimerQueue timers,
    EntityPresenceReplicator presence,
    ActiveSimulationSessionStore sessionStore,
    CharacterItemStateStore itemStateStore,
    SimulationEntityRegistry entityRegistry,
    CarryStateSynchronizer carrySync,
    RealtimeSessionTeardown teardown,
    AuthorityOperationScheduler authorityScheduler,
    RealtimeOperationTracker operationTracker,
    GatheringAuthorityOperations authority,
    WorldActorMetrics? metrics) : IRealtimeCompletionHandler
{
    // A character that moves farther than this from where gathering started stops.
    private const float MovementTolerance = 0.1f;

    // An uncertain GameService outcome retries with the same operation id this often.
    private const int MaximumUncertainRetries = 3;

    private readonly CommittedCarryApplier carryApplier = new(itemStateStore, sessionStore);
    private readonly Dictionary<int, GatheringActivity> activities = [];
    private readonly List<GatheringActivity> activityBuffer = [];

    public int ActiveCount => activities.Count;

    // Starts gathering after the interaction result of a StartGathering action is final.
    public void Begin(
        IRealtimePeerConnection peer,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        GatheringStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (activities.TryGetValue(context.PeerId, out var existing))
        {
            Stop(existing, GatheringStopCodes.Interrupted, "Gathering moved to another node.");
        }

        if (!peers.TryGetBoundPlayer(context, out var player)
            || start.Node.ResourceProfile is null)
        {
            return;
        }

        var profile = start.Node.ResourceProfile;
        var activity = new GatheringActivity(
            context,
            session,
            start.InteractionSessionId,
            start.Node,
            start.CapabilityId,
            player.Movement.State.PositionX,
            player.Movement.State.PositionZ)
        {
            NextCycleTick = clock.ServerTick + CycleTicks(profile.CycleSeconds)
        };
        activities.Add(context.PeerId, activity);
        metrics?.SetActiveGatherers(activities.Count);
        sender.SendControl(peer, RealtimeProtocol.EncodeGatheringState(RealtimeGatheringState.CreateStarted(
            activity.InteractionSessionId,
            activity.Node.NetworkEntityId,
            activity.Node.RuntimeActorId,
            CycleMilliseconds(profile.CycleSeconds))));
    }

    // Runs every tick after the interaction leases were revalidated.
    public void Process()
    {
        if (activities.Count == 0)
        {
            return;
        }

        activityBuffer.Clear();
        activityBuffer.AddRange(activities.Values);
        var serverTick = clock.ServerTick;
        foreach (var activity in activityBuffer)
        {
            if (!activities.TryGetValue(activity.Context.PeerId, out var current)
                || !ReferenceEquals(current, activity))
            {
                continue;
            }

            if (!IsCurrent(activity, out _, out var player))
            {
                Remove(activity);
                continue;
            }

            if (activity.CycleQueued)
            {
                continue;
            }

            if (HasMoved(activity, player))
            {
                Stop(activity, GatheringStopCodes.Interrupted, "Moving stopped gathering.");
                continue;
            }

            if (leases is null || !leases.MatchesWorldActor(
                    activity.Context.PeerId,
                    activity.InteractionSessionId,
                    activity.Node))
            {
                Stop(activity, GatheringStopCodes.InteractionClosed, "The interaction with the node closed.");
                continue;
            }

            if (serverTick < activity.NextCycleTick)
            {
                continue;
            }

            if (!activity.Node.IsActive)
            {
                Stop(activity, GatheringStopCodes.Depleted, "The resource node is depleted.");
                continue;
            }

            // Every remaining charge may be reserved by other gatherers whose yields are in
            // flight; try again shortly instead of failing.
            if (!activity.Node.TryReserveResourceCharge())
            {
                activity.NextCycleTick = serverTick + 1;
                continue;
            }

            activity.Generation = activity.Node.ResourceGeneration;
            activity.CycleQueued = true;
            activity.CycleStarted = false;
            if (activity.OperationId == Guid.Empty)
            {
                activity.OperationId = Guid.NewGuid();
            }

            authorityScheduler.Enqueue(activity.Context, new QueuedGatheringCycle(this, activity));
        }
    }

    public void Complete(RealtimeOperationResult completion)
    {
        if (completion is GatheringCycleCompleted completed)
        {
            FinalizeCycle(completed);
        }
    }

    // Peers that are gone release charges reserved for cycles that never started.
    public void Clear()
    {
        foreach (var activity in activities.Values.ToArray())
        {
            Remove(activity);
        }
    }

    private void StartCycle(RealtimePeerContext context, GatheringActivity activity)
    {
        if (!activities.TryGetValue(context.PeerId, out var current)
            || !ReferenceEquals(current, activity)
            || !IsCurrent(activity, out _, out _))
        {
            activity.Node.ReleaseResourceCharge(activity.Generation);
            activity.CycleQueued = false;
            context.AuthorityOperationInFlight = false;
            authorityScheduler.StartNext(context);
            return;
        }

        activity.CycleStarted = true;
        operationTracker.Track(authority.RunAsync(
            this,
            context,
            activity.Session,
            activity.Node,
            activity.Generation,
            new SimulationGatheringRequest(
                activity.OperationId,
                activity.InteractionSessionId,
                activity.CapabilityId,
                activity.Node.Spawn.Id,
                activity.Node.ResourceProfile!)));
    }

    private void FinalizeCycle(GatheringCycleCompleted completed)
    {
        completed.Context.AuthorityOperationInFlight = false;
        var node = completed.Node;
        var activity = activities.TryGetValue(completed.Context.PeerId, out var current)
            && current.OperationId == completed.Request.OperationId
                ? current
                : null;
        if (activity is not null)
        {
            activity.CycleQueued = false;
            activity.CycleStarted = false;
        }

        if (completed.Result.Succeeded)
        {
            metrics?.RecordGatheringCycle("yielded");
            var depleted = node.ConsumeResourceCharge(completed.Generation);
            if (depleted)
            {
                timers.Schedule(
                    clock.ServerTick + CycleTicks(node.ResourceProfile!.RespawnSeconds),
                    node.RuntimeActorId,
                    node.ResourceGeneration);
                presence.BroadcastWorldActorState(node);
            }

            FinalizeYield(completed, activity);
            if (depleted)
            {
                StopEveryoneAt(node);
            }
        }
        else
        {
            node.ReleaseResourceCharge(completed.Generation);
            FinalizeRejection(completed, activity);
        }

        authorityScheduler.StartNext(completed.Context);
    }

    private void FinalizeYield(GatheringCycleCompleted completed, GatheringActivity? activity)
    {
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId != completed.Session.SimulationSessionId)
        {
            return;
        }

        if (!CommittedCarryApplier.TryGetCommittedState(completed.Result.Transaction!, out var committed))
        {
            TerminateForDivergence(peer, completed);
            return;
        }

        var applyResult = carryApplier.Apply(completed.Session, committed, out var carry);
        if (CommittedCarryApplier.IsDiverged(applyResult))
        {
            TerminateForDivergence(peer, completed);
            return;
        }

        // The change goes before the carry state, so the client patches its inventory instead
        // of reloading it.
        var change = applyResult == ItemStateApplyResult.Applied
            ? OwnerItemStateChangeMapper.TryCreate(Guid.Empty, completed.Session.CharacterId, completed.Result.Transaction!)
            : null;
        if (change is not null)
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeOwnerItemStateChanged(change));
        }

        if (entityRegistry.TryGetPlayerByCharacterId(completed.Session.CharacterId, out var entity))
        {
            carrySync.ApplyLatest(entity!);
        }

        var profile = completed.Request.Profile;
        if (activity is not null)
        {
            activity.OperationId = Guid.Empty;
            activity.UncertainRetries = 0;
            activity.NextCycleTick = clock.ServerTick + CycleTicks(profile.CycleSeconds);
        }

        // The yield committed even when gathering stopped while it was in flight, so the client
        // always learns the item-state revision that holds it.
        sender.SendControl(peer, RealtimeProtocol.EncodeGatheringState(RealtimeGatheringState.CreateYielded(
            completed.Request.InteractionSessionId,
            completed.Node.NetworkEntityId,
            completed.Node.RuntimeActorId,
            CycleMilliseconds(profile.CycleSeconds),
            profile.YieldDefinitionId,
            profile.YieldQuantity,
            carry.ItemStateRevision)));
    }

    private void TerminateForDivergence(IRealtimePeerConnection peer, GatheringCycleCompleted completed)
    {
        teardown.TerminateForAuthorityFailure(
            peer,
            completed.Context,
            completed.Session,
            "item_state_diverged",
            "Committed gathering state diverged from the active simulation state.");
    }

    private void FinalizeRejection(GatheringCycleCompleted completed, GatheringActivity? activity)
    {
        var error = completed.Result.Error!;
        if (!peers.TryGetCurrentPeer(completed.Context, out var peer)
            || completed.Context.Session is null
            || completed.Context.Session.SimulationSessionId != completed.Session.SimulationSessionId)
        {
            return;
        }

        if (GameServiceOutcome.EndsItemSession(error.Code))
        {
            metrics?.RecordGatheringCycle("rejected");
            teardown.TerminateForAuthorityFailure(
                peer,
                completed.Context,
                completed.Session,
                error.Code,
                error.Message);
            return;
        }

        if (activity is null)
        {
            metrics?.RecordGatheringCycle("rejected");
            return;
        }

        // The grant may have committed before the answer was lost, so the same operation id
        // replays its stored result on the next cycle instead of granting twice.
        if (GameServiceOutcome.IsUncertain(error.Code)
            && activity.UncertainRetries < MaximumUncertainRetries)
        {
            metrics?.RecordGatheringCycle("retried");
            activity.UncertainRetries++;
            activity.NextCycleTick = clock.ServerTick + CycleTicks(completed.Request.Profile.CycleSeconds);
            return;
        }

        metrics?.RecordGatheringCycle("rejected");
        Stop(activity, error.Code, error.Message);
    }

    // Depletion stops everyone gathering at the node, including cycles still in flight,
    // whose completions only release their reservation.
    private void StopEveryoneAt(WorldActorRuntimeState node)
    {
        foreach (var activity in activities.Values.ToArray())
        {
            if (ReferenceEquals(activity.Node, node))
            {
                Stop(activity, GatheringStopCodes.Depleted, "The resource node is depleted.");
            }
        }
    }

    private void Stop(GatheringActivity activity, string code, string message)
    {
        Remove(activity);
        if (peers.TryGetCurrentPeer(activity.Context, out var peer))
        {
            sender.SendControl(peer, RealtimeProtocol.EncodeGatheringState(RealtimeGatheringState.CreateStopped(
                activity.InteractionSessionId,
                activity.Node.NetworkEntityId,
                activity.Node.RuntimeActorId,
                code,
                message)));
        }
    }

    private void Remove(GatheringActivity activity)
    {
        if (activities.TryGetValue(activity.Context.PeerId, out var current)
            && ReferenceEquals(current, activity))
        {
            activities.Remove(activity.Context.PeerId);
        }

        // A queued cycle that never started still holds its reservation; a started one
        // releases or consumes it when its completion arrives.
        if (activity.CycleQueued && !activity.CycleStarted)
        {
            activity.Node.ReleaseResourceCharge(activity.Generation);
            activity.CycleQueued = false;
        }

        metrics?.SetActiveGatherers(activities.Count);
    }

    private bool IsCurrent(
        GatheringActivity activity,
        out IRealtimePeerConnection? peer,
        out PlayerSimulationEntity player)
    {
        player = null!;
        if (!peers.TryGetCurrentPeer(activity.Context, out peer)
            || activity.Context.Session is null
            || activity.Context.Session.SimulationSessionId != activity.Session.SimulationSessionId
            || !peers.TryGetBoundPlayer(activity.Context, out var bound))
        {
            return false;
        }

        player = bound;
        return true;
    }

    private static bool HasMoved(GatheringActivity activity, PlayerSimulationEntity player)
    {
        var state = player.Movement.State;
        var deltaX = state.PositionX - activity.StartX;
        var deltaZ = state.PositionZ - activity.StartZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ) > MovementTolerance * MovementTolerance;
    }

    private uint CycleTicks(float seconds)
    {
        return (uint)Math.Max(1, Math.Ceiling(seconds * config.MovementSimulation.TickRateHz));
    }

    private static int CycleMilliseconds(float seconds)
    {
        return (int)Math.Ceiling(seconds * 1000f);
    }

    private sealed class GatheringActivity(
        RealtimePeerContext context,
        ActiveSimulationSession session,
        Guid interactionSessionId,
        WorldActorRuntimeState node,
        string capabilityId,
        float startX,
        float startZ)
    {
        public RealtimePeerContext Context { get; } = context;

        public ActiveSimulationSession Session { get; } = session;

        public Guid InteractionSessionId { get; } = interactionSessionId;

        public WorldActorRuntimeState Node { get; } = node;

        public string CapabilityId { get; } = capabilityId;

        public float StartX { get; } = startX;

        public float StartZ { get; } = startZ;

        public uint NextCycleTick { get; set; }

        public bool CycleQueued { get; set; }

        public bool CycleStarted { get; set; }

        public long Generation { get; set; }

        public Guid OperationId { get; set; }

        public int UncertainRetries { get; set; }
    }

    private sealed record QueuedGatheringCycle(
        GatheringHandler Owner,
        GatheringActivity Activity) : IRealtimeAuthorityOperation
    {
        public void Start(
            RealtimePeerContext context,
            PlayerSimulationEntity entity,
            PlayerMovementState state)
        {
            Owner.StartCycle(context, Activity);
        }

        // A cycle that never started gives its reserved charge back.
        public void Cancel(RealtimePeerContext context, string code, string message)
        {
            Owner.Remove(Activity);
        }
    }
}
