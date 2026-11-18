using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the interest manager and replicates entity presence: baselines, spawns,
// despawns and world actor state. Every spawn it sends raises the spawn hooks.
//
// It also decides who gets a change of a combatant's state, a player's look, or a world actor's
// state at once: the viewers that see the entity in detail, and every viewer for an urgent
// change. The others missed that state, as the interest manager remembers per viewer and entity,
// and get its newest version when the entity comes into detail for them, and otherwise on the
// entity's slot once a second, which FarStateRefresher runs. A spawn that cannot be built costs
// that entity for that connection only: it is logged and counted, and the connection does not see
// the entity, so nothing about it reaches a client that never got its spawn. Loop-confined, like
// the interest manager.
internal sealed class EntityPresenceReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    WorldActorStore? worldActorStore,
    SimulationInterestManager interestManager,
    RealtimeNetworkMetrics networkMetrics,
    RealtimeSimulationClock clock,
    RealtimeLifecycleHooks hooks,
    ILogger? logger = null)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<WorldActorRuntimeState> worldActorBuffer = [];
    private readonly List<SimulationInterestEntity> interestEntityBuffer = [];
    private readonly List<int> actorStateRecipients = [];
    private Func<ulong, bool>? isVisibleToAnyConnection;
    private uint refreshCycle;
    private long reportedDeferredByBurst;
    private long reportedDeferredByQueue;

    public int ActiveEntityCount => entityRegistry.PlayerCount + (worldActorStore?.Count ?? 0);

    // The looks that player spawns carry.
    public AppearanceReplicator? Appearances { get; set; }

    // Whether a world actor walks, so the pose its spawn carries is not a rest pose; without it
    // no actor walks.
    public Func<WorldActorRuntimeState, bool>? IsActorMoving { get; set; }

    // A combatant's current state, encoded, or null for an entity that is no combatant; without
    // it no viewer gets a combatant state it missed.
    public Func<ulong, byte[]?>? EncodeCombatantState { get; set; }

    public bool IsVisibleTo(int connectionId, ulong entityId)
    {
        return interestManager.GetVisible(connectionId).Contains(entityId);
    }

    // A world actor that appeared after activation, such as a respawned Mob, enters the
    // interest index and spawns for every connection that now sees it.
    public void TrackSpawnedWorldActor(WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        foreach (var connectionId in interestManager.AddEntity(new SimulationInterestEntity(
                     actor.NetworkEntityId,
                     actor.PositionX,
                     actor.PositionZ)))
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                SendWorldActorSpawn(peer, actor);
            }
        }

        networkMetrics.SetActiveEntities(ActiveEntityCount);
    }

    // A teleported player, such as one that respawned, is despawned and spawned again for
    // every connection that sees it, so clients snap instead of interpolating the jump.
    public void RespawnEntityForViewers(PlayerSimulationEntity entity, string reason)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !interestManager.GetVisible(context.PeerId).Contains(entity.NetworkEntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            SendDespawn(peer, entity.NetworkEntityId, reason);
            SendSpawn(peer, entity);
        }
    }

    // A despawned world actor leaves every connection that saw it at once.
    public void BroadcastWorldActorDespawn(WorldActorRuntimeState actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(actor);
        foreach (var connectionId in interestManager.ForgetEntity(actor.NetworkEntityId))
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                SendWorldActorDespawn(peer, actor, reason);
            }
        }

        PruneDespawned();
        networkMetrics.SetActiveEntities(ActiveEntityCount);
    }

    // Adds a newly registered entity to the interest index, or moves a reused one, and
    // returns the connections that now see a new entity.
    public IReadOnlyList<int> TrackJoiningEntity(
        PlayerSimulationEntity entity,
        bool isNewEntity)
    {
        IReadOnlyList<int> connectionsEnteringNewEntity;
        var interestEntity = RealtimeStateMapper.ToInterestEntity(entity);
        if (isNewEntity)
        {
            connectionsEnteringNewEntity = interestManager.AddEntity(interestEntity);
        }
        else
        {
            interestManager.UpdateEntity(interestEntity);
            connectionsEnteringNewEntity = Array.Empty<int>();
        }

        return connectionsEnteringNewEntity;
    }

    public SimulationInterestUpdate RefreshConnection(int connectionId, ulong controlledEntityId)
    {
        return interestManager.Refresh(connectionId, controlledEntityId);
    }

    public IReadOnlyList<ulong> GetVisibleOrdered(int connectionId)
    {
        return interestManager.GetVisibleOrdered(connectionId);
    }

    // The connection's visible entities ascending by id, whether each is near, and the version of
    // the list, for the snapshot path; empty for a connection without interest.
    public void GetVisibleSpans(
        int connectionId,
        out ReadOnlySpan<ulong> orderedVisible,
        out ReadOnlySpan<bool> orderedNear,
        out ulong version)
    {
        interestManager.TryGetVisibleSpans(connectionId, out orderedVisible, out orderedNear, out version);
    }

    public void ForgetConnection(int connectionId)
    {
        interestManager.RemoveConnection(connectionId);
    }

    public void Clear()
    {
        interestManager.Clear();
    }

    // The spawns of the entities in the connection's first view, in the order given, which is
    // nearest first for the entities that its first refresh brought in.
    public void SendEntityBaseline(IRealtimePeerConnection peer, IReadOnlyList<ulong> entityIds)
    {
        for (var index = 0; index < entityIds.Count; index++)
        {
            var entityId = entityIds[index];
            if (entityRegistry.TryGetPlayer(entityId, out var entity))
            {
                SendSpawn(peer, entity!);
            }
            else if (worldActorStore is not null
                && worldActorStore.TryGetByEntityId(entityId, out var actor))
            {
                SendWorldActorSpawn(peer, actor!);
            }
        }
    }

    public void SendEntityBaseline(IRealtimePeerConnection peer, IReadOnlySet<ulong> visibleEntityIds)
    {
        entityRegistry.CopyPlayersTo(playerBuffer);
        foreach (var entity in playerBuffer)
        {
            if (visibleEntityIds.Contains(entity.NetworkEntityId))
            {
                SendSpawn(peer, entity);
            }
        }

        if (worldActorStore is null)
        {
            return;
        }

        worldActorStore.CopyActorsTo(worldActorBuffer);
        foreach (var actor in worldActorBuffer)
        {
            if (visibleEntityIds.Contains(actor.NetworkEntityId))
            {
                SendWorldActorSpawn(peer, actor);
            }
        }
    }

    public void SendEntitySpawnToConnections(
        PlayerSimulationEntity entity,
        IReadOnlyList<int> connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                SendSpawn(peer, entity);
            }
        }
    }

    // Refreshes the connections whose turn it is; every connection gets one refresh per
    // RefreshIntervalSnapshots calls. Joins refresh their connection at once. Returns the
    // Stopwatch ticks it spent sending the spawns and despawns, for the interest phase's parts.
    public long RefreshInterests()
    {
        var interval = interestManager.RefreshIntervalSnapshots;
        var turn = (int)(unchecked(refreshCycle++) % (uint)interval);
        long presenceTicks = 0;
        foreach (var context in peers.Contexts)
        {
            if (context.PeerId % interval != turn
                || context.Session is null
                || !connectionBindings.TryGetEntityId(context.PeerId, out var controlledEntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            // Indexed loops, because enumerating the interfaces would allocate an enumerator.
            var update = interestManager.Refresh(context.PeerId, controlledEntityId);
            if (update.Entered.Count == 0 && update.Exited.Count == 0 && update.DetailEntered.Count == 0)
            {
                continue;
            }

            // The entities that entered go nearest first.
            var sendsStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < update.EnteredNearestFirst.Count; index++)
            {
                var enteredEntityId = update.EnteredNearestFirst[index];
                if (entityRegistry.TryGetPlayer(enteredEntityId, out var enteredEntity))
                {
                    SendSpawn(peer, enteredEntity!);
                }
                else if (worldActorStore is not null
                    && worldActorStore.TryGetByEntityId(
                        enteredEntityId,
                        out var enteredActor))
                {
                    SendWorldActorSpawn(peer, enteredActor!);
                }
            }

            for (var index = 0; index < update.Exited.Count; index++)
            {
                var exitedEntityId = update.Exited[index];
                if (worldActorStore is not null
                    && worldActorStore.TryGetByEntityId(
                        exitedEntityId,
                        out var exitedActor))
                {
                    SendWorldActorDespawn(
                        peer,
                        exitedActor!,
                        "out_of_interest");
                }
                else if (worldActorStore is not null
                    && worldActorStore.TryGetDespawnedByEntityId(
                        exitedEntityId,
                        out var despawnedActor))
                {
                    SendWorldActorDespawn(
                        peer,
                        despawnedActor!,
                        "despawned");
                }
                else
                {
                    SendDespawn(peer, exitedEntityId, "out_of_interest");
                }
            }

            // What came into detail brings every state the connection missed of it at once.
            for (var index = 0; index < update.DetailEntered.Count; index++)
            {
                CatchUp(peer, context.PeerId, update.DetailEntered[index]);
            }

            presenceTicks += Stopwatch.GetTimestamp() - sendsStarted;
        }

        PruneDespawned();
        ReportDeferredEntries();
        return presenceTicks;
    }

    // Tells the metrics how many entities waited for a later refresh since the last report, by
    // why they waited.
    private void ReportDeferredEntries()
    {
        var byBurst = interestManager.DeferredByBurstCount;
        var byQueue = interestManager.DeferredByQueueCount;
        if (byBurst != reportedDeferredByBurst || byQueue != reportedDeferredByQueue)
        {
            networkMetrics.RecordInterestEntriesDeferred(
                byBurst - reportedDeferredByBurst,
                byQueue - reportedDeferredByQueue);
            reportedDeferredByBurst = byBurst;
            reportedDeferredByQueue = byQueue;
        }
    }

    // Drops world actor tombstones that no connection still sees.
    public void PruneDespawned()
    {
        worldActorStore?.PruneDespawned(isVisibleToAnyConnection ??= interestManager.IsVisibleToAnyConnection);
    }

    public void BroadcastEntityDespawn(
        ulong entityId,
        string reason,
        int? excludedConnectionId)
    {
        // A send that throws for one connection must not keep the despawn from the others, who
        // would show the entity for good. The first exception is thrown again after the last send.
        var affectedConnections = interestManager.ForgetEntity(entityId);
        ExceptionDispatchInfo? failure = null;
        foreach (var connectionId in affectedConnections)
        {
            if (connectionId != excludedConnectionId
                && peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                try
                {
                    SendDespawn(peer, entityId, reason);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        failure?.Throw();
    }

    private RealtimeEntitySpawn ToRealtimeEntitySpawn(PlayerSimulationEntity entity)
    {
        return new RealtimeEntitySpawn(
            entity.NetworkEntityId,
            RealtimeEntityKind.Player,
            entity.Session.CharacterId.ToString("D"),
            entity.Session.CharacterName,
            entity.ArchetypeId,
            clock.ServerTick,
            RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State),
            Appearances?.SpawnAppearanceOf(entity)
                ?? new RealtimeCharacterAppearance(AppearanceReplicationSettings.Default.DefaultBodyModel, [], [], false, string.Empty));
    }

    // Brings the interest index to the players and world actors as they are now; only what moved
    // to another cell, came, or went changes more than its position.
    public void SynchronizeInterestIndex(IReadOnlyList<PlayerSimulationEntity> players)
    {
        interestEntityBuffer.Clear();
        for (var index = 0; index < players.Count; index++)
        {
            var entity = players[index];
            interestEntityBuffer.Add(new SimulationInterestEntity(
                entity.NetworkEntityId,
                entity.Movement.State.PositionX,
                entity.Movement.State.PositionZ));
        }

        if (worldActorStore is not null)
        {
            worldActorStore.CopyActorsTo(worldActorBuffer);
            for (var index = 0; index < worldActorBuffer.Count; index++)
            {
                var actor = worldActorBuffer[index];
                interestEntityBuffer.Add(new SimulationInterestEntity(
                    actor.NetworkEntityId,
                    actor.PositionX,
                    actor.PositionZ));
            }
        }

        interestManager.Synchronize(CollectionsMarshal.AsSpan(interestEntityBuffer));
    }

    // A spawn carries the entity's newest look and state, and its combat state follows it, so the
    // connection missed nothing of it afterwards.
    private void SendSpawn(IRealtimePeerConnection peer, PlayerSimulationEntity entity)
    {
        interestManager.TakeMissed(peer.Id, entity.NetworkEntityId);
        byte[] packet;
        try
        {
            packet = RealtimeProtocol.EncodeEntitySpawn(ToRealtimeEntitySpawn(entity));
        }
        catch (Exception exception) when (CanWithhold(peer, entity.NetworkEntityId, exception))
        {
            WithholdAfterFailedSpawn(peer, entity.NetworkEntityId, exception);
            return;
        }

        sender.SendControl(peer, packet);
        networkMetrics.RecordSpawnPacket();
        hooks.RaiseSpawnSent(peer, entity.NetworkEntityId);
    }

    private void SendWorldActorSpawn(
        IRealtimePeerConnection peer,
        WorldActorRuntimeState actor)
    {
        // The pose is the one of the current tick, the tick that snapshots name.
        interestManager.TakeMissed(peer.Id, actor.NetworkEntityId);
        byte[] packet;
        try
        {
            packet = RealtimeProtocol.EncodeWorldActorSpawn(
                WorldActorProtocolMapper.ToSpawn(
                    actor,
                    clock.ServerTick,
                    IsActorMoving?.Invoke(actor) ?? false));
        }
        catch (Exception exception) when (CanWithhold(peer, actor.NetworkEntityId, exception))
        {
            WithholdAfterFailedSpawn(peer, actor.NetworkEntityId, exception);
            return;
        }

        sender.SendControl(peer, packet);
        networkMetrics.RecordSpawnPacket();
        hooks.RaiseSpawnSent(peer, actor.NetworkEntityId);
    }

    // A spawn that cannot be built costs its entity for this connection, but not the connection's
    // own entity, whose spawn the join needs: that failure ends the join, as before.
    private bool CanWithhold(IRealtimePeerConnection peer, ulong entityId, Exception exception)
    {
        return exception is not OutOfMemoryException
            && !(connectionBindings.TryGetEntityId(peer.Id, out var controlledEntityId) && controlledEntityId == entityId);
    }

    // The connection does not see the entity any more, so no snapshot, state, event, or despawn
    // names an entity that its client never got, and the entity stays out while it is within the
    // connection's enter radius, so it is not built and failed again at every refresh.
    private void WithholdAfterFailedSpawn(IRealtimePeerConnection peer, ulong entityId, Exception exception)
    {
        interestManager.Withhold(peer.Id, entityId);
        networkMetrics.RecordSpawnFailure();
        logger?.LogError(
            exception,
            "[SIMULATION] The spawn of entity {EntityId} could not be built for UDP peer {PeerId}, so that peer does not see it.",
            entityId,
            peer.Id);
    }

    private void SendWorldActorDespawn(
        IRealtimePeerConnection peer,
        WorldActorRuntimeState actor,
        string reason)
    {
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeWorldActorDespawn(
                new RealtimeWorldActorDespawn(
                    actor.NetworkEntityId,
                    actor.RuntimeActorId,
                    reason)));
        networkMetrics.RecordDespawnPacket();
    }

    // Sends the actor's state to the viewers that see it in detail, and to every viewer when it
    // became active or inactive, such as a depleted or returned resource node.
    public void BroadcastWorldActorState(WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var urgent = actor.IsActive != actor.ReplicatedIsActive;
        actor.ReplicatedIsActive = actor.IsActive;
        CollectChangeRecipients(actor.NetworkEntityId, MissedStates.WorldActor, urgent, -1, actorStateRecipients);
        byte[]? packet = null;
        foreach (var connectionId in actorStateRecipients)
        {
            if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                packet ??= RealtimeProtocol.EncodeWorldActorState(WorldActorProtocolMapper.ToState(actor));
                sender.SendControl(peer, packet);
            }
        }
    }

    // Fills recipients with the connections that get a change of the entity's states at once:
    // those that see the entity in detail, or every one that sees it when the change is urgent.
    // The others missed the states; with None, as for a combat event, they miss nothing. The
    // excluded connection, such as an owner that gets its own look apart, is left out.
    public void CollectChangeRecipients(
        ulong entityId,
        MissedStates states,
        bool urgent,
        int excludedConnectionId,
        List<int> recipients)
    {
        interestManager.CollectRecipients(entityId, states, urgent, excludedConnectionId, recipients);
    }

    // The entity that a combatant targets, or 0: a viewer sees its target and whoever targets it
    // in detail at any distance.
    public void SetTarget(ulong entityId, ulong targetEntityId)
    {
        interestManager.SetTarget(entityId, targetEntityId);
    }

    // Sends the connection the newest of every state it missed of the entity at once, as when
    // the entity comes into detail for it.
    public void CatchUp(int connectionId, ulong entityId)
    {
        if (peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
        {
            CatchUp(peer, connectionId, entityId);
        }
    }

    // What the connections missed of the entities on this slot, grouped by entity; they missed
    // nothing afterwards.
    public void TakeMissedOnSlot(uint slotPhase, uint slotCount, List<SimulationInterestMissed> destination)
    {
        interestManager.TakeMissedOnSlot(slotPhase, slotCount, destination);
    }

    // The connection missed these states of the entity again.
    public void MarkMissed(int connectionId, ulong entityId, MissedStates states)
    {
        interestManager.MarkMissed(connectionId, entityId, states);
    }

    // The newest state of one kind of the entity, encoded, or null when the entity has none. A
    // Mob's world actor state is held back while the Mob walks, since a client takes every world
    // actor state of a Mob as the pose it rests at; the state after its next stop replaces it.
    public byte[]? EncodeNewestState(ulong entityId, MissedStates kind, out bool heldBack)
    {
        heldBack = false;
        switch (kind)
        {
            case MissedStates.Combatant:
                return EncodeCombatantState?.Invoke(entityId);
            case MissedStates.Look:
                return Appearances?.EncodeLook(entityId);
            case MissedStates.WorldActor:
                if (worldActorStore is null || !worldActorStore.TryGetByEntityId(entityId, out var actor))
                {
                    return null;
                }

                if (actor!.IsActive && (IsActorMoving?.Invoke(actor) ?? false))
                {
                    heldBack = true;
                    return null;
                }

                return RealtimeProtocol.EncodeWorldActorState(WorldActorProtocolMapper.ToState(actor));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "One state kind is expected.");
        }
    }

    private void CatchUp(IRealtimePeerConnection peer, int connectionId, ulong entityId)
    {
        var missed = interestManager.TakeMissed(connectionId, entityId);
        var heldBack = MissedStates.None;
        foreach (var kind in MissedStateKinds.All)
        {
            if ((missed & kind) == 0)
            {
                continue;
            }

            var packet = EncodeNewestState(entityId, kind, out var held);
            if (held)
            {
                heldBack |= kind;
            }
            else if (packet is not null)
            {
                sender.SendControl(peer, packet);
            }
        }

        interestManager.MarkMissed(connectionId, entityId, heldBack);
    }

    private void SendDespawn(IRealtimePeerConnection peer, ulong entityId, string reason)
    {
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(entityId, reason)));
        networkMetrics.RecordDespawnPacket();
    }
}
