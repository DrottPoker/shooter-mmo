using System.Diagnostics;

namespace Mmorpg.Tools.SimulationBotClient;

// How often a bot received the state of each remote player it saw, inside a measurement window.
// A remote player counts from its reliable spawn to its despawn. Each remote snapshot frame
// that carries its state counts once, by snapshot sequence, so a duplicated or reordered packet
// never counts twice. Records of entities without a player spawn, such as moving Mobs, count
// only as untracked records, and the bot's own entity never counts. A visit, a remote player's
// stay from its spawn to its despawn, folds into the bot's totals and into a histogram of visit
// rates when it ends, so what the bot keeps grows with the players in its interest at once, not
// with the run's length, the players it met, or how often they left and came back.
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
    private readonly SimulationBotRateHistogram visitRates = new();
    private int visits;
    private long updates;
    private double visibleSeconds;
    private long nearMovingUpdates;
    private double nearMovingSeconds;
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
            visits,
            updates,
            visibleSeconds,
            untrackedRecords,
            nearMovingUpdates,
            nearMovingSeconds,
            visitRates.Copy());
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

        visits++;
        updates += entity.Updates;
        visibleSeconds += (endTimestamp - from) / (double)Stopwatch.Frequency;
        nearMovingUpdates += entity.NearMovingUpdates;
        nearMovingSeconds += entity.NearMovingSeconds;
        if (entity.NearMovingSeconds > 0)
        {
            visitRates.Add(entity.NearMovingUpdates / entity.NearMovingSeconds);
        }
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
}

public sealed record SimulationBotRemoteCadenceSummary(
    int Visits,
    long Updates,
    double VisibleSeconds,
    long UntrackedRecords,
    long NearMovingUpdates,
    double NearMovingSeconds,
    SimulationBotRateHistogram VisitRates)
{
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

        var rates = first.VisitRates.Copy();
        rates.Add(second.VisitRates);
        return new SimulationBotRemoteCadenceSummary(
            first.Visits + second.Visits,
            first.Updates + second.Updates,
            first.VisibleSeconds + second.VisibleSeconds,
            first.UntrackedRecords + second.UntrackedRecords,
            first.NearMovingUpdates + second.NearMovingUpdates,
            first.NearMovingSeconds + second.NearMovingSeconds,
            rates);
    }
}

// Rates in bins of 0.05 Hz from 0 to 60 Hz, a rate of 60 Hz or more in the last bin, so a
// percentile over any number of rates needs a fixed few kilobytes. The bins are allocated with
// the first rate.
public sealed class SimulationBotRateHistogram
{
    public const double BinHz = 0.05;
    public const int Bins = 1200;

    private int[]? counts;

    public long Count { get; private set; }

    public void Add(double hz)
    {
        if (double.IsNaN(hz) || hz < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(hz));
        }

        counts ??= new int[Bins];
        counts[(int)Math.Min(Bins - 1, Math.Floor(hz / BinHz))]++;
        Count++;
    }

    public void Add(SimulationBotRateHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.counts is null)
        {
            return;
        }

        counts ??= new int[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            counts[bin] += other.counts[bin];
        }

        Count += other.Count;
    }

    public SimulationBotRateHistogram Copy()
    {
        return new SimulationBotRateHistogram
        {
            counts = (int[]?)counts?.Clone(),
            Count = Count
        };
    }

    // The middle of the bin that holds the rate at the percentile, counted as the sorted rate at
    // index ceiling(percentile * count) - 1; null without rates.
    public double? Percentile(double percentile)
    {
        if (counts is null || Count == 0)
        {
            return null;
        }

        var index = Math.Clamp((long)Math.Ceiling(percentile * Count) - 1, 0, Count - 1);
        var seen = 0L;
        for (var bin = 0; bin < Bins; bin++)
        {
            seen += counts[bin];
            if (seen > index)
            {
                return (bin + 0.5d) * BinHz;
            }
        }

        return (Bins - 0.5d) * BinHz;
    }
}
