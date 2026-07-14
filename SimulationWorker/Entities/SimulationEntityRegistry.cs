using SimulationWorker.Realtime;
using SimulationWorker.Sessions;

namespace SimulationWorker.Entities;

public sealed class SimulationEntityRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<ulong, PlayerSimulationEntity> playersByEntityId = [];
    private readonly Dictionary<Guid, ulong> entityIdsByCharacterId = [];
    private ulong nextEntityId = 1;

    public SimulationEntityRegistration RegisterPlayer(
        ActiveSimulationSession session,
        Func<AuthoritativePlayerMovement> movementFactory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(movementFactory);

        lock (syncRoot)
        {
            if (entityIdsByCharacterId.TryGetValue(session.CharacterId, out var existingEntityId))
            {
                var existing = playersByEntityId[existingEntityId];
                if (existing.Session.SimulationSessionId == session.SimulationSessionId)
                {
                    existing.RefreshSession(session);
                    return new SimulationEntityRegistration(existing, false, null);
                }

                var replacement = CreatePlayer(session, movementFactory);
                playersByEntityId.Remove(existingEntityId);
                playersByEntityId.Add(replacement.NetworkEntityId, replacement);
                entityIdsByCharacterId[session.CharacterId] = replacement.NetworkEntityId;
                return new SimulationEntityRegistration(replacement, true, existing);
            }

            var entity = CreatePlayer(session, movementFactory);
            playersByEntityId.Add(entity.NetworkEntityId, entity);
            entityIdsByCharacterId.Add(session.CharacterId, entity.NetworkEntityId);
            return new SimulationEntityRegistration(entity, true, null);
        }
    }

    public bool TryGetPlayer(ulong entityId, out PlayerSimulationEntity? entity)
    {
        lock (syncRoot)
        {
            return playersByEntityId.TryGetValue(entityId, out entity);
        }
    }

    public bool TryGetPlayerByCharacterId(Guid characterId, out PlayerSimulationEntity? entity)
    {
        lock (syncRoot)
        {
            if (!entityIdsByCharacterId.TryGetValue(characterId, out var entityId))
            {
                entity = null;
                return false;
            }

            entity = playersByEntityId[entityId];
            return true;
        }
    }

    public bool RemovePlayer(
        ulong entityId,
        Guid expectedSimulationSessionId,
        out PlayerSimulationEntity? entity)
    {
        lock (syncRoot)
        {
            if (!playersByEntityId.TryGetValue(entityId, out entity)
                || entity.Session.SimulationSessionId != expectedSimulationSessionId)
            {
                entity = null;
                return false;
            }

            playersByEntityId.Remove(entityId);
            entityIdsByCharacterId.Remove(entity.Session.CharacterId);
            return true;
        }
    }

    public IReadOnlyCollection<PlayerSimulationEntity> ListPlayers()
    {
        lock (syncRoot)
        {
            return playersByEntityId.Values
                .OrderBy(entity => entity.NetworkEntityId)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            playersByEntityId.Clear();
            entityIdsByCharacterId.Clear();
        }
    }

    private ulong AllocateEntityId()
    {
        if (nextEntityId == 0)
        {
            throw new InvalidOperationException("Simulation entity id space is exhausted.");
        }

        return nextEntityId++;
    }

    private PlayerSimulationEntity CreatePlayer(
        ActiveSimulationSession session,
        Func<AuthoritativePlayerMovement> movementFactory)
    {
        var entityId = AllocateEntityId();
        return new PlayerSimulationEntity(entityId, session, movementFactory());
    }
}

public sealed record SimulationEntityRegistration(
    PlayerSimulationEntity Entity,
    bool IsNewEntity,
    PlayerSimulationEntity? ReplacedEntity);
