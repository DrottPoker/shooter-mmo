namespace Mmorpg.SimulationWorker.Realtime;

// One reporting interval of the realtime metrics. The periodic log lines and the optional
// performance file are both written from it, so they always carry the same values.
// Totals holds the counters since the worker started; Delta holds the counters of this
// interval and the current gauges.
internal sealed record RealtimeMetricsInterval(
    DateTime StartedAtUtc,
    DateTime EndedAtUtc,
    double Seconds,
    double MachineCpuPercent,
    double SingleCoreCpuPercent,
    long WorkingSetBytes,
    RealtimeNetworkMetricsSnapshot Totals,
    RealtimeNetworkMetricsSnapshot Delta,
    RealtimePerformanceMetricsSnapshot Performance)
{
    // The Mob numbers of the interval, when the worker's combat and world actor metrics exist.
    public RealtimeMobActivity? Mobs { get; init; }

    // The counters of current minus previous, never negative, with the gauges of current.
    public static RealtimeNetworkMetricsSnapshot Subtract(
        RealtimeNetworkMetricsSnapshot current,
        RealtimeNetworkMetricsSnapshot previous)
    {
        return current with
        {
            ReceivedPackets = NonNegative(current.ReceivedPackets, previous.ReceivedPackets),
            ReceivedBytes = NonNegative(current.ReceivedBytes, previous.ReceivedBytes),
            SentPackets = NonNegative(current.SentPackets, previous.SentPackets),
            SentBytes = NonNegative(current.SentBytes, previous.SentBytes),
            QuotaRejectedPackets = NonNegative(current.QuotaRejectedPackets, previous.QuotaRejectedPackets),
            DroppedSnapshotPackets = NonNegative(current.DroppedSnapshotPackets, previous.DroppedSnapshotPackets),
            BackpressureDroppedSnapshotPackets = NonNegative(
                current.BackpressureDroppedSnapshotPackets,
                previous.BackpressureDroppedSnapshotPackets),
            AcceptedJoins = NonNegative(current.AcceptedJoins, previous.AcceptedJoins),
            RejectedJoins = NonNegative(current.RejectedJoins, previous.RejectedJoins),
            SpawnPackets = NonNegative(current.SpawnPackets, previous.SpawnPackets),
            DespawnPackets = NonNegative(current.DespawnPackets, previous.DespawnPackets),
            SnapshotEntityRecords = NonNegative(current.SnapshotEntityRecords, previous.SnapshotEntityRecords),
            SnapshotBytes = NonNegative(current.SnapshotBytes, previous.SnapshotBytes)
        };
    }

    private static long NonNegative(long current, long previous)
    {
        return Math.Max(0, current - previous);
    }
}

// The living Mobs of the worker, the ones that chase, attack, or evade, the ones that moved in
// the last tick, the routes that wait for the planning budget, and the Mob routes planned in
// the interval.
internal sealed record RealtimeMobActivity(
    long Mobs,
    long EngagedMobs,
    long MovingMobs,
    long MobPathQueue,
    long MobPathPlans);
