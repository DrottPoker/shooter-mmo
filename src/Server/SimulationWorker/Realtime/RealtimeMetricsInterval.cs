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
