using WorldServer.Realtime;
using WorldServer.Sessions;

namespace WorldServer.Entities;

public sealed class WorldEntityRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<ulong, PlayerWorldEntity> playersByEntityId = [];
    private readonly Dictionary<Guid, ulong> entityIdsByCharacterId = [];
    private ulong nextEntityId = 1;

    public WorldEntityRegistration RegisterPlayer(
        ActivePlayerSession session,
        Func<AuthoritativePlayerMovement> movementFactory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(movementFactory);

        lock (syncRoot)
        {
            if (entityIdsByCharacterId.TryGetValue(session.CharacterId, out var existingEntityId))
            {
                var existing = playersByEntityId[existingEntityId];
                if (existing.Session.WorldSessionId == session.WorldSessionId)
                {
                    existing.RefreshSession(session);
                    return new WorldEntityRegistration(existing, false, null);
                }

                var replacement = CreatePlayer(session, movementFactory);
                playersByEntityId.Remove(existingEntityId);
                playersByEntityId.Add(replacement.NetworkEntityId, replacement);
                entityIdsByCharacterId[session.CharacterId] = replacement.NetworkEntityId;
                return new WorldEntityRegistration(replacement, true, existing);
            }

            var entity = CreatePlayer(session, movementFactory);
            playersByEntityId.Add(entity.NetworkEntityId, entity);
            entityIdsByCharacterId.Add(session.CharacterId, entity.NetworkEntityId);
            return new WorldEntityRegistration(entity, true, null);
        }
    }

    public bool TryGetPlayer(ulong entityId, out PlayerWorldEntity? entity)
    {
        lock (syncRoot)
        {
            return playersByEntityId.TryGetValue(entityId, out entity);
        }
    }

    public bool TryGetPlayerByCharacterId(Guid characterId, out PlayerWorldEntity? entity)
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
        Guid expectedWorldSessionId,
        out PlayerWorldEntity? entity)
    {
        lock (syncRoot)
        {
            if (!playersByEntityId.TryGetValue(entityId, out entity)
                || entity.Session.WorldSessionId != expectedWorldSessionId)
            {
                entity = null;
                return false;
            }

            playersByEntityId.Remove(entityId);
            entityIdsByCharacterId.Remove(entity.Session.CharacterId);
            return true;
        }
    }

    public IReadOnlyCollection<PlayerWorldEntity> ListPlayers()
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
            throw new InvalidOperationException("World entity id space is exhausted.");
        }

        return nextEntityId++;
    }

    private PlayerWorldEntity CreatePlayer(
        ActivePlayerSession session,
        Func<AuthoritativePlayerMovement> movementFactory)
    {
        var entityId = AllocateEntityId();
        return new PlayerWorldEntity(entityId, session, movementFactory());
    }
}

public sealed record WorldEntityRegistration(
    PlayerWorldEntity Entity,
    bool IsNewEntity,
    PlayerWorldEntity? ReplacedEntity);
