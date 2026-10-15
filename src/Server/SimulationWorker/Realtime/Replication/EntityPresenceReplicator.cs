using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the interest manager and replicates entity presence: baselines, spawns,
// despawns and world actor state. Loop-confined, like the interest manager.
internal sealed class EntityPresenceReplicator(
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    WorldActorStore? worldActorStore,
    SimulationInterestManager interestManager,
    RealtimeNetworkMetrics networkMetrics,
    RealtimeSimulationClock clock)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<WorldActorRuntimeState> worldActorBuffer = [];
    private readonly List<SimulationInterestEntity> interestEntityBuffer = [];

    public int ActiveEntityCount => entityRegistry.PlayerCount + (worldActorStore?.Count ?? 0);

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

    public void RefreshInterests(int? excludedConnectionId)
    {
        foreach (var context in peers.Contexts)
        {
            if (context.PeerId == excludedConnectionId
                || context.Session is null
                || !connectionBindings.TryGetEntityId(context.PeerId, out var controlledEntityId)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var update = interestManager.Refresh(context.PeerId, controlledEntityId);
            foreach (var enteredEntityId in update.Entered)
            {
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

            foreach (var exitedEntityId in update.Exited)
            {
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
        }

        PruneDespawned();
    }

    // Drops world actor tombstones that no connection still sees.
    public void PruneDespawned()
    {
        worldActorStore?.PruneDespawned(interestManager.IsVisibleToAnyConnection);
    }

    public void BroadcastEntityDespawn(
        ulong entityId,
        string reason,
        int? excludedConnectionId)
    {
        var affectedConnections = interestManager.ForgetEntity(entityId);
        foreach (var connectionId in affectedConnections)
        {
            if (connectionId != excludedConnectionId
                && peers.TryGetAuthenticatedPeer(connectionId, out _, out var peer))
            {
                SendDespawn(peer, entityId, reason);
            }
        }
    }

    private RealtimeEntitySpawn ToRealtimeEntitySpawn(PlayerSimulationEntity entity)
    {
        return new RealtimeEntitySpawn(
            entity.NetworkEntityId,
            RealtimeEntityKind.Player,
            entity.Session.CharacterId.ToString("D"),
            entity.Session.CharacterName,
            PlayerSimulationEntity.DefaultArchetypeId,
            clock.ServerTick,
            RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State));
    }

    public void RebuildInterestIndex(IReadOnlyList<PlayerSimulationEntity> players)
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

        interestManager.Rebuild(interestEntityBuffer);
    }

    private void SendSpawn(IRealtimePeerConnection peer, PlayerSimulationEntity entity)
    {
        sender.SendControl(peer, RealtimeProtocol.EncodeEntitySpawn(ToRealtimeEntitySpawn(entity)));
        networkMetrics.RecordSpawnPacket();
    }

    private void SendWorldActorSpawn(
        IRealtimePeerConnection peer,
        WorldActorRuntimeState actor)
    {
        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeWorldActorSpawn(
                WorldActorProtocolMapper.ToSpawn(actor)));
        networkMetrics.RecordSpawnPacket();
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
