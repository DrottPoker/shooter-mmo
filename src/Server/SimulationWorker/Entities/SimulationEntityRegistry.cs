using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Entities;

// The players of the Shard by network entity id and by character, and the counter that gives out
// the ids of players and world actors. An id is never given out twice, not even after its entity
// left, so a client never mistakes a new entity for an old one; the counter therefore only grows,
// and a worker that has used all of them cannot take a player or respawn a Mob until it restarts.
public sealed class SimulationEntityRegistry
{
    // An exhausted counter logs once a minute at most, since every join and respawn then fails.
    private static readonly TimeSpan ExhaustionLogInterval = TimeSpan.FromMinutes(1);

    private readonly object syncRoot = new();
    private readonly Dictionary<ulong, PlayerSimulationEntity> playersByEntityId = [];
    private readonly Dictionary<Guid, ulong> entityIdsByCharacterId = [];
    private readonly ILogger? logger;
    private ulong nextEntityId = 1;
    private long lastExhaustionLogTimestamp;

    public SimulationEntityRegistry(ILogger<SimulationEntityRegistry>? logger = null)
    {
        this.logger = logger;
    }

    // Starts the id counter elsewhere, so a test can reach the end of the id space.
    internal SimulationEntityRegistry(ulong nextEntityId, ILogger? logger = null)
        : this(null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nextEntityId);
        this.nextEntityId = nextEntityId;
        this.logger = logger;
    }

    // The ids given out since the worker started, the share of the id space that is gone.
    public long IssuedEntityIds
    {
        get
        {
            lock (syncRoot)
            {
                return (long)Math.Min(nextEntityId - 1, uint.MaxValue);
            }
        }
    }

    public int PlayerCount
    {
        get
        {
            lock (syncRoot)
            {
                return playersByEntityId.Count;
            }
        }
    }

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

    public ulong AllocateNetworkEntityId()
    {
        lock (syncRoot)
        {
            return AllocateEntityId();
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

    public void CopyPlayersTo(List<PlayerSimulationEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (syncRoot)
        {
            destination.Clear();
            destination.AddRange(playersByEntityId.Values);
            destination.Sort(static (left, right) =>
                left.NetworkEntityId.CompareTo(right.NetworkEntityId));
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

    // The remote snapshot record carries the id in 32 bits, so the counter stops there.
    private ulong AllocateEntityId()
    {
        if (nextEntityId > uint.MaxValue)
        {
            LogExhaustion();
            throw new InvalidOperationException(
                $"Simulation entity ids are exhausted: a snapshot record carries at most {uint.MaxValue} as an entity id.");
        }

        return nextEntityId++;
    }

    private void LogExhaustion()
    {
        var now = Stopwatch.GetTimestamp();
        if (lastExhaustionLogTimestamp != 0 && Stopwatch.GetElapsedTime(lastExhaustionLogTimestamp, now) < ExhaustionLogInterval)
        {
            return;
        }

        lastExhaustionLogTimestamp = now;
        logger?.LogCritical(
            "[SIMULATION] All {IdCount} simulation entity ids are used, so no player can join and no Mob can respawn on this worker. Restart the worker, which starts the ids again, as docs/MONITORING.md describes.",
            uint.MaxValue);
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
