using System.Diagnostics;

namespace Mmorpg.Tools.SimulationBotClient;

// How often a bot received the state of each remote player it saw, inside a measurement window.
// A remote player counts from its reliable spawn to its despawn. Each remote snapshot frame
// that carries its state counts once, by snapshot sequence, so a duplicated or reordered packet
// never counts twice. Records of entities without a player spawn, such as moving Mobs, count
// only as untracked records, and the bot's own entity never counts. The windows of one remote
// player add up, so what the bot keeps grows with the players it saw, not with the run's length.
//
// The worker sends a record only when it is due, so a player that stands gets about one per
// second. The rate that judges replication is therefore the near moving rate: a record whose
// position differs from the one before, while the one before also moved, and while the player
// is within NearRadius of the bot, counts as one update over the time since the record before.
// The rate is those updates over that time, so a player that walks near the bot is judged by
// the gaps between its records while it walks.
public sealed class SimulationBotRemoteCadence(float nearRadius = SimulationBotRemoteCadence.DefaultNearRadius)
{
    // The worker's default InterestManagement:NearRadius.
    public const float DefaultNearRadius = 48f;

    // Less than one step of the record does not move.
    private const float MovedDistanceSquared = 0.0001f;

    private readonly float nearRadiusSquared = nearRadius * nearRadius;
    private readonly Dictionary<ulong, TrackedEntity> visible = [];
    private readonly Dictionary<ulong, EntityWindow> closed = [];
    private ulong? ownEntityId;
    private long? windowStart;
    private long? windowEnd;
    private long untrackedRecords;

    // The bot's own entity is not a remote player, even when the worker spawns it.
    public void SetOwnEntity(ulong entityId)
    {
        ownEntityId = entityId;
        visible.Remove(entityId);
    }

    public void OnSpawn(ulong entityId, long nowTimestamp)
    {
        if (entityId == ownEntityId || visible.ContainsKey(entityId))
        {
            return;
        }

        visible[entityId] = new TrackedEntity(entityId, nowTimestamp);
    }

    public void OnDespawn(ulong entityId, long nowTimestamp)
    {
        if (!visible.Remove(entityId, out var entity))
        {
            return;
        }

        Close(entity, nowTimestamp);
    }

    // A record of an entity without its position, which counts for the rate of every entity
    // only.
    public void OnRecord(ulong entityId, uint snapshotSequence, long nowTimestamp)
    {
        OnRecord(entityId, snapshotSequence, nowTimestamp, null, default);
    }

    // A record at x and z while the bot stands at own.
    public void OnRecord(
        ulong entityId,
        uint snapshotSequence,
        long nowTimestamp,
        (float X, float Z)? position,
        (float X, float Z) own)
    {
        if (entityId == ownEntityId)
        {
            return;
        }

        if (!visible.TryGetValue(entityId, out var entity))
        {
            if (IsInsideWindow(nowTimestamp))
            {
                untrackedRecords++;
            }

            return;
        }

        if (entity.HasSequence && snapshotSequence <= entity.LastSequence)
        {
            return;
        }

        entity.HasSequence = true;
        entity.LastSequence = snapshotSequence;
        var inside = IsInsideWindow(nowTimestamp);
        if (inside)
        {
            entity.Updates++;
        }

        if (position is not { } at)
        {
            return;
        }

        var moved = entity.HasPosition
            && Square(at.X - entity.X) + Square(at.Z - entity.Z) > MovedDistanceSquared;
        var near = Square(at.X - own.X) + Square(at.Z - own.Z) <= nearRadiusSquared;
        if (inside && moved && entity.LastMoved && near && entity.LastRecordAt >= windowStart)
        {
            entity.NearMovingUpdates++;
            entity.NearMovingSeconds += (nowTimestamp - entity.LastRecordAt) / (double)Stopwatch.Frequency;
        }

        entity.HasPosition = true;
        entity.X = at.X;
        entity.Z = at.Z;
        entity.LastMoved = moved;
        entity.LastRecordAt = nowTimestamp;
    }

    public void BeginWindow(long nowTimestamp)
    {
        if (windowStart is not null)
        {
            return;
        }

        windowStart = nowTimestamp;
    }

    public void EndWindow(long nowTimestamp)
    {
        if (windowStart is null || windowEnd is not null)
        {
            return;
        }

        windowEnd = nowTimestamp;
        foreach (var entity in visible.Values)
        {
            Close(entity, nowTimestamp);
            entity.Updates = 0;
            entity.NearMovingUpdates = 0;
            entity.NearMovingSeconds = 0;
            entity.Closed = true;
        }
    }

    // Null until the window ended.
    public SimulationBotRemoteCadenceSummary? Capture()
    {
        if (windowStart is null || windowEnd is null)
        {
            return null;
        }

        return new SimulationBotRemoteCadenceSummary(
            closed.Count,
            closed.Values.Sum(entity => entity.Updates),
            closed.Values.Sum(entity => entity.VisibleSeconds),
            untrackedRecords,
            closed.Values
                .Select(entity => new SimulationBotEntityCadence(
                    entity.Updates,
                    entity.VisibleSeconds,
                    entity.NearMovingUpdates,
                    entity.NearMovingSeconds))
                .ToArray());
    }

    private static float Square(float value)
    {
        return value * value;
    }

    private bool IsInsideWindow(long timestamp)
    {
        return windowStart is { } start && timestamp >= start && windowEnd is null;
    }

    private void Close(TrackedEntity entity, long endTimestamp)
    {
        if (entity.Closed || windowStart is not { } start || windowEnd is { } end && endTimestamp > end)
        {
            return;
        }

        var from = Math.Max(start, entity.SpawnedAt);
        if (endTimestamp <= from)
        {
            return;
        }

        if (!closed.TryGetValue(entity.EntityId, out var window))
        {
            window = new EntityWindow();
            closed.Add(entity.EntityId, window);
        }

        window.Updates += entity.Updates;
        window.VisibleSeconds += (endTimestamp - from) / (double)Stopwatch.Frequency;
        window.NearMovingUpdates += entity.NearMovingUpdates;
        window.NearMovingSeconds += entity.NearMovingSeconds;
    }

    private sealed class TrackedEntity(ulong entityId, long spawnedAt)
    {
        public ulong EntityId { get; } = entityId;

        public long SpawnedAt { get; } = spawnedAt;

        public bool HasSequence { get; set; }

        public uint LastSequence { get; set; }

        public long Updates { get; set; }

        public bool HasPosition { get; set; }

        public float X { get; set; }

        public float Z { get; set; }

        // Whether the newest record moved from the one before it.
        public bool LastMoved { get; set; }

        public long LastRecordAt { get; set; }

        public long NearMovingUpdates { get; set; }

        public double NearMovingSeconds { get; set; }

        public bool Closed { get; set; }
    }

    private sealed class EntityWindow
    {
        public long Updates { get; set; }

        public double VisibleSeconds { get; set; }

        public long NearMovingUpdates { get; set; }

        public double NearMovingSeconds { get; set; }
    }
}

// One remote player that a bot saw inside the window: the states it received and how long it saw
// it, and the updates it got while the player walked near, with the time they covered.
public sealed record SimulationBotEntityCadence(
    long Updates,
    double VisibleSeconds,
    long NearMovingUpdates = 0,
    double NearMovingSeconds = 0);

public sealed record SimulationBotRemoteCadenceSummary(
    int Entities,
    long Updates,
    double VisibleSeconds,
    long UntrackedRecords,
    IReadOnlyList<SimulationBotEntityCadence> PerEntity)
{
    public long NearMovingUpdates => PerEntity.Sum(entity => entity.NearMovingUpdates);

    public double NearMovingSeconds => PerEntity.Sum(entity => entity.NearMovingSeconds);

    // Updates per second of the remote players while they walked near the bot; null when none
    // did.
    public double? UpdatesPerSecond => NearMovingSeconds > 0 ? NearMovingUpdates / NearMovingSeconds : null;

    // Records per second over every remote player the bot saw, weighted by how long it saw each,
    // standing or walking, near or far.
    public double? AllEntitiesUpdatesPerSecond => VisibleSeconds > 0 ? Updates / VisibleSeconds : null;

    // The measurements of two connections of one bot, such as before and after it left the game
    // and came back.
    public static SimulationBotRemoteCadenceSummary? Combine(
        SimulationBotRemoteCadenceSummary? first,
        SimulationBotRemoteCadenceSummary? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        return new SimulationBotRemoteCadenceSummary(
            first.Entities + second.Entities,
            first.Updates + second.Updates,
            first.VisibleSeconds + second.VisibleSeconds,
            first.UntrackedRecords + second.UntrackedRecords,
            [.. first.PerEntity, .. second.PerEntity]);
    }
}
