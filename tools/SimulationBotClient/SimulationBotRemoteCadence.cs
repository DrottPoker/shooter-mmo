using System.Diagnostics;

namespace Mmorpg.Tools.SimulationBotClient;

// How often a bot received the state of each remote player it saw, inside a measurement window.
// A remote player counts from its reliable spawn to its despawn. Each remote snapshot frame
// that carries its state counts once, by snapshot sequence, so a duplicated or reordered packet
// never counts twice. Records of entities without a player spawn, such as moving Mobs, count
// only as untracked records, and the bot's own entity never counts.
public sealed class SimulationBotRemoteCadence
{
    private readonly Dictionary<ulong, TrackedEntity> visible = [];
    private readonly List<EntityWindow> closed = [];
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

        visible[entityId] = new TrackedEntity(nowTimestamp);
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
            closed.Sum(entity => entity.Updates),
            closed.Sum(entity => entity.VisibleSeconds),
            untrackedRecords,
            closed.Select(entity => new SimulationBotEntityCadence(entity.Updates, entity.VisibleSeconds)).ToArray());
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

        closed.Add(new EntityWindow(
            entity.Updates,
            (endTimestamp - from) / (double)Stopwatch.Frequency));
    }

    private sealed class TrackedEntity(long spawnedAt)
    {
        public long SpawnedAt { get; } = spawnedAt;

        public bool HasSequence { get; set; }

        public uint LastSequence { get; set; }

        public long Updates { get; set; }

        public bool Closed { get; set; }
    }

    private sealed record EntityWindow(long Updates, double VisibleSeconds);
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
}
