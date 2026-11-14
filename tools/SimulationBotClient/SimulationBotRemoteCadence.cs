using System.Diagnostics;

namespace Mmorpg.Tools.SimulationBotClient;

// How often a bot received the state of each remote player it saw, inside a measurement window.
// A remote player counts from its reliable spawn to its despawn. Each remote snapshot frame
// that carries its state counts once, by snapshot sequence, so a duplicated or reordered packet
// never counts twice. Records of entities without a player spawn, such as moving Mobs, count
// only as untracked records, and the bot's own entity never counts. The windows of one remote
// player add up, so what the bot keeps grows with the players it saw, not with the run's length.
public sealed class SimulationBotRemoteCadence
{
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

    public void OnRecord(ulong entityId, uint snapshotSequence, long nowTimestamp)
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
        if (IsInsideWindow(nowTimestamp))
        {
            entity.Updates++;
        }
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
            closed.Values.Select(entity => new SimulationBotEntityCadence(entity.Updates, entity.VisibleSeconds)).ToArray());
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
    }

    private sealed class TrackedEntity(ulong entityId, long spawnedAt)
    {
        public ulong EntityId { get; } = entityId;

        public long SpawnedAt { get; } = spawnedAt;

        public bool HasSequence { get; set; }

        public uint LastSequence { get; set; }

        public long Updates { get; set; }

        public bool Closed { get; set; }
    }

    private sealed class EntityWindow
    {
        public long Updates { get; set; }

        public double VisibleSeconds { get; set; }
    }
}

// One remote player that a bot saw inside the window: the states it received and how long it saw it.
public sealed record SimulationBotEntityCadence(long Updates, double VisibleSeconds);

public sealed record SimulationBotRemoteCadenceSummary(
    int Entities,
    long Updates,
    double VisibleSeconds,
    long UntrackedRecords,
    IReadOnlyList<SimulationBotEntityCadence> PerEntity)
{
    // Updates per second over every remote player the bot saw, weighted by how long it saw each.
    public double? UpdatesPerSecond => VisibleSeconds > 0 ? Updates / VisibleSeconds : null;

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
