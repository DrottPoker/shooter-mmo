using System.Diagnostics.Metrics;

namespace SimulationWorker.Realtime;

public readonly record struct RealtimeNetworkMetricsSnapshot(
    long ActivePeers,
    long ActiveEntities,
    long ReceivedPackets,
    long ReceivedBytes,
    long SentPackets,
    long SentBytes,
    long QuotaRejectedPackets,
    long DroppedSnapshotPackets,
    long AcceptedJoins,
    long RejectedJoins,
    long SpawnPackets,
    long DespawnPackets,
    long SnapshotEntityRecords);

public sealed class RealtimeNetworkMetrics : IDisposable
{
    public const string MeterName = "ShooterMmo.SimulationWorker.Realtime";

    private readonly Meter meter = new(MeterName);
    private long activePeers;
    private long activeEntities;
    private long receivedPackets;
    private long receivedBytes;
    private long sentPackets;
    private long sentBytes;
    private long quotaRejectedPackets;
    private long droppedSnapshotPackets;
    private long acceptedJoins;
    private long rejectedJoins;
    private long spawnPackets;
    private long despawnPackets;
    private long snapshotEntityRecords;

    public RealtimeNetworkMetrics()
    {
        meter.CreateObservableGauge("simulation_worker.realtime.peers", () => Volatile.Read(ref activePeers));
        meter.CreateObservableGauge("simulation_worker.realtime.entities", () => Volatile.Read(ref activeEntities));
        meter.CreateObservableCounter("simulation_worker.realtime.received.packets", () => Volatile.Read(ref receivedPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.received.bytes", () => Volatile.Read(ref receivedBytes));
        meter.CreateObservableCounter("simulation_worker.realtime.sent.packets", () => Volatile.Read(ref sentPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.sent.bytes", () => Volatile.Read(ref sentBytes));
        meter.CreateObservableCounter("simulation_worker.realtime.quota_rejected.packets", () => Volatile.Read(ref quotaRejectedPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot_dropped.packets", () => Volatile.Read(ref droppedSnapshotPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.joins.accepted", () => Volatile.Read(ref acceptedJoins));
        meter.CreateObservableCounter("simulation_worker.realtime.joins.rejected", () => Volatile.Read(ref rejectedJoins));
        meter.CreateObservableCounter("simulation_worker.realtime.spawn.packets", () => Volatile.Read(ref spawnPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.despawn.packets", () => Volatile.Read(ref despawnPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot.entity_records", () => Volatile.Read(ref snapshotEntityRecords));
    }

    public void SetActivePeers(long value) => Interlocked.Exchange(ref activePeers, value);

    public void SetActiveEntities(long value) => Interlocked.Exchange(ref activeEntities, value);

    public void RecordReceived(int bytes)
    {
        Interlocked.Increment(ref receivedPackets);
        Interlocked.Add(ref receivedBytes, bytes);
    }

    public void RecordSent(int bytes)
    {
        Interlocked.Increment(ref sentPackets);
        Interlocked.Add(ref sentBytes, bytes);
    }

    public void RecordQuotaRejected() => Interlocked.Increment(ref quotaRejectedPackets);

    public void RecordSnapshotDropped() => Interlocked.Increment(ref droppedSnapshotPackets);

    public void RecordJoinAccepted() => Interlocked.Increment(ref acceptedJoins);

    public void RecordJoinRejected() => Interlocked.Increment(ref rejectedJoins);

    public void RecordSpawnPacket() => Interlocked.Increment(ref spawnPackets);

    public void RecordDespawnPacket() => Interlocked.Increment(ref despawnPackets);

    public void RecordSnapshotEntities(int count) => Interlocked.Add(ref snapshotEntityRecords, count);

    public RealtimeNetworkMetricsSnapshot Capture()
    {
        return new RealtimeNetworkMetricsSnapshot(
            Volatile.Read(ref activePeers),
            Volatile.Read(ref activeEntities),
            Volatile.Read(ref receivedPackets),
            Volatile.Read(ref receivedBytes),
            Volatile.Read(ref sentPackets),
            Volatile.Read(ref sentBytes),
            Volatile.Read(ref quotaRejectedPackets),
            Volatile.Read(ref droppedSnapshotPackets),
            Volatile.Read(ref acceptedJoins),
            Volatile.Read(ref rejectedJoins),
            Volatile.Read(ref spawnPackets),
            Volatile.Read(ref despawnPackets),
            Volatile.Read(ref snapshotEntityRecords));
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
