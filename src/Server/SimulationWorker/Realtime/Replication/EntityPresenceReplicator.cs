using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the interest manager and replicates entity presence: baselines, spawns,
// despawns and world actor state. Every spawn it sends raises the spawn hooks.
// Loop-confined, like the interest manager.
internal sealed class EntityPresenceReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    WorldActorStore? worldActorStore,
    SimulationInterestManager interestManager,
    RealtimeNetworkMetrics networkMetrics,
    RealtimeSimulationClock clock,
    RealtimeLifecycleHooks hooks)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<WorldActorRuntimeState> worldActorBuffer = [];
    private readonly List<SimulationInterestEntity> interestEntityBuffer = [];
    private Func<ulong, bool>? isVisibleToAnyConnection;
    private uint refreshCycle;

    public int ActiveEntityCount => entityRegistry.PlayerCount + (worldActorStore?.Count ?? 0);

    // The looks that player spawns carry.
    public AppearanceReplicator? Appearances { get; set; }

    // Whether a world actor walks, so the pose its spawn carries is not a rest pose; without it
    // no actor walks.
    public Func<WorldActorRuntimeState, bool>? IsActorMoving { get; set; }

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
            if (update.Entered.Count == 0 && update.Exited.Count == 0)
            {
                continue;
            }

            var sendsStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < update.Entered.Count; index++)
            {
                var enteredEntityId = update.Entered[index];
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

            presenceTicks += Stopwatch.GetTimestamp() - sendsStarted;
        }

        PruneDespawned();
        return presenceTicks;
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

    private void SendSpawn(IRealtimePeerConnection peer, PlayerSimulationEntity entity)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeEntitySpawn(ToRealtimeEntitySpawn(entity)));
        networkMetrics.RecordSpawnPacket();
        hooks.RaiseSpawnSent(peer, entity.NetworkEntityId);
    }

    private void SendWorldActorSpawn(
        IRealtimePeerConnection peer,
        WorldActorRuntimeState actor)
    {
        // The pose is the one of the current tick, the tick that snapshots name.
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeWorldActorSpawn(
                WorldActorProtocolMapper.ToSpawn(
                    actor,
                    clock.ServerTick,
                    IsActorMoving?.Invoke(actor) ?? false)));
        networkMetrics.RecordSpawnPacket();
        hooks.RaiseSpawnSent(peer, actor.NetworkEntityId);
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

    public void BroadcastWorldActorState(WorldActorRuntimeState actor)
    {
        var packet = RealtimeProtocol.EncodeWorldActorState(
            WorldActorProtocolMapper.ToState(actor));
        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !interestManager.GetVisible(context.PeerId)
                    .Contains(actor.NetworkEntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            sender.SendControl(peer, packet);
        }
    }

    private void SendDespawn(IRealtimePeerConnection peer, ulong entityId, string reason)
    {
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(entityId, reason)));
        networkMetrics.RecordDespawnPacket();
    }
}
