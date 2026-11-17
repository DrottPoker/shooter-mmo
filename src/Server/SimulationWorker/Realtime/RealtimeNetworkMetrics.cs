using System.Diagnostics.Metrics;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

public readonly record struct RealtimeNetworkMetricsSnapshot(
    long ActivePeers,
    long ActiveEntities,
    long ActiveRealPlayers,
    long ActiveSyntheticBots,
    long UnauthenticatedPeers,
    long ReceivedPackets,
    long ReceivedBytes,
    long SentPackets,
    long SentBytes,
    long QuotaRejectedPackets,
    long DroppedSnapshotPackets,
    long BackpressureDroppedSnapshotPackets,
    long AcceptedJoins,
    long RejectedJoins,
    long SpawnPackets,
    long DespawnPackets,
    long SnapshotEntityRecords,
    long SnapshotBytes)
{
    // The sent and received messages and bytes of every kind with at least one message, in
    // ascending type byte order.
    public IReadOnlyList<RealtimeMessageKindTraffic> SentByMessage { get; init; } = [];

    public IReadOnlyList<RealtimeMessageKindTraffic> ReceivedByMessage { get; init; } = [];

    // The UDP datagrams that crossed the transport's sealing layer, which carries every message,
    // LiteNetLib's headers, acknowledgements, and pings, merged or alone: received ones count
    // also when the layer dropped them. Their bytes include the layer's own bytes, which
    // SealingOverheadBytes holds for both directions. All 0 without a transport.
    public long DatagramsReceived { get; init; }

    public long DatagramsSent { get; init; }

    public long DatagramBytesReceived { get; init; }

    public long DatagramBytesSent { get; init; }

    public long SealingOverheadBytes { get; init; }

    // The received datagrams that the layer dropped: malformed, unauthenticated, replayed, or
    // clear where only sealed ones count.
    public long DroppedDatagrams { get; init; }
}

public sealed class RealtimeNetworkMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.Realtime";

    // The tag of the per-kind message counters, the RealtimeMessageType name.
    public const string MessageTagName = "message";

    // The tag of the dropped datagram counter.
    public const string DropReasonTagName = "reason";

    private static readonly (RealtimeDatagramDrop Reason, string Name)[] DropReasons =
    [
        (RealtimeDatagramDrop.Malformed, "malformed"),
        (RealtimeDatagramDrop.Unauthenticated, "unauthenticated"),
        (RealtimeDatagramDrop.Replayed, "replayed"),
        (RealtimeDatagramDrop.ClearAfterSealing, "clear_after_sealing"),
        (RealtimeDatagramDrop.ClearNotAllowed, "clear_not_allowed")
    ];

    private readonly Meter meter = new(MeterName);
    private long activePeers;
    private long activeEntities;
    private long activeRealPlayers;
    private long activeSyntheticBots;
    private long unauthenticatedPeers;
    private long receivedPackets;
    private long receivedBytes;
    private long sentPackets;
    private long sentBytes;
    private long quotaRejectedPackets;
    private long rejectedConnections;
    private long droppedSnapshotPackets;
    private long backpressureDroppedSnapshotPackets;
    private long acceptedJoins;
    private long rejectedJoins;
    private long spawnPackets;
    private long despawnPackets;
    private long snapshotEntityRecords;
    private long snapshotBytes;

    // Indexed by the type byte of the message, as RealtimeMessageKinds reads it.
    private readonly long[] receivedMessagesByKind = new long[RealtimeMessageKinds.Count];
    private readonly long[] receivedBytesByKind = new long[RealtimeMessageKinds.Count];
    private readonly long[] sentMessagesByKind = new long[RealtimeMessageKinds.Count];
    private readonly long[] sentBytesByKind = new long[RealtimeMessageKinds.Count];

    // The transport's datagram counts, once the transport started.
    private RealtimeTransportSecurityCounters? datagrams;

    public RealtimeNetworkMetrics()
    {
        meter.CreateObservableGauge("simulation_worker.realtime.peers", () => Volatile.Read(ref activePeers));
        meter.CreateObservableGauge("simulation_worker.realtime.entities", () => Volatile.Read(ref activeEntities));
        meter.CreateObservableGauge("simulation_worker.realtime.players.real", () => Volatile.Read(ref activeRealPlayers));
        meter.CreateObservableGauge("simulation_worker.realtime.players.synthetic", () => Volatile.Read(ref activeSyntheticBots));
        meter.CreateObservableGauge("simulation_worker.realtime.peers.unauthenticated", () => Volatile.Read(ref unauthenticatedPeers));
        meter.CreateObservableCounter("simulation_worker.realtime.received.packets", () => Volatile.Read(ref receivedPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.received.bytes", () => Volatile.Read(ref receivedBytes));
        meter.CreateObservableCounter("simulation_worker.realtime.sent.packets", () => Volatile.Read(ref sentPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.sent.bytes", () => Volatile.Read(ref sentBytes));
        meter.CreateObservableCounter("simulation_worker.realtime.quota_rejected.packets", () => Volatile.Read(ref quotaRejectedPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.connections.rejected", () => Volatile.Read(ref rejectedConnections));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot_dropped.packets", () => Volatile.Read(ref droppedSnapshotPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot_backpressure_dropped.packets", () => Volatile.Read(ref backpressureDroppedSnapshotPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.joins.accepted", () => Volatile.Read(ref acceptedJoins));
        meter.CreateObservableCounter("simulation_worker.realtime.joins.rejected", () => Volatile.Read(ref rejectedJoins));
        meter.CreateObservableCounter("simulation_worker.realtime.spawn.packets", () => Volatile.Read(ref spawnPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.despawn.packets", () => Volatile.Read(ref despawnPackets));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot.entity_records", () => Volatile.Read(ref snapshotEntityRecords));
        meter.CreateObservableCounter("simulation_worker.realtime.snapshot.bytes", () => Volatile.Read(ref snapshotBytes));
        meter.CreateObservableCounter("simulation_worker.realtime.received.messages", () => ObserveKinds(receivedMessagesByKind));
        meter.CreateObservableCounter("simulation_worker.realtime.sent.messages", () => ObserveKinds(sentMessagesByKind));
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.received", () => Volatile.Read(ref datagrams)?.DatagramsReceived ?? 0);
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.sent", () => Volatile.Read(ref datagrams)?.DatagramsSent ?? 0);
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.received.bytes", () => Volatile.Read(ref datagrams)?.BytesReceived ?? 0);
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.sent.bytes", () => Volatile.Read(ref datagrams)?.BytesSent ?? 0);
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.overhead.bytes", () => OverheadBytes(Volatile.Read(ref datagrams)));
        meter.CreateObservableCounter("simulation_worker.realtime.datagrams.dropped", ObserveDrops);
    }

    // The counts of the transport's sealing layer, which the transport attaches when it starts.
    public void AttachTransport(RealtimeTransportSecurityCounters counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        Volatile.Write(ref datagrams, counters);
    }

    public void SetActivePeers(long value) => Interlocked.Exchange(ref activePeers, value);

    public void SetActiveEntities(long value) => Interlocked.Exchange(ref activeEntities, value);

    public void SetPeerPopulation(
        long activeRealPlayerCount,
        long activeSyntheticBotCount,
        long unauthenticatedPeerCount)
    {
        Interlocked.Exchange(ref activeRealPlayers, activeRealPlayerCount);
        Interlocked.Exchange(ref activeSyntheticBots, activeSyntheticBotCount);
        Interlocked.Exchange(ref unauthenticatedPeers, unauthenticatedPeerCount);
    }

    // Counts one received message under the kind its header names.
    public void RecordReceived(ReadOnlySpan<byte> packet)
    {
        var kind = RealtimeMessageKinds.Of(packet);
        Interlocked.Increment(ref receivedPackets);
        Interlocked.Add(ref receivedBytes, packet.Length);
        Interlocked.Increment(ref receivedMessagesByKind[kind]);
        Interlocked.Add(ref receivedBytesByKind[kind], packet.Length);
    }

    // Counts one message handed to the transport under the kind its header names.
    public void RecordSent(ReadOnlySpan<byte> packet)
    {
        var kind = RealtimeMessageKinds.Of(packet);
        Interlocked.Increment(ref sentPackets);
        Interlocked.Add(ref sentBytes, packet.Length);
        Interlocked.Increment(ref sentMessagesByKind[kind]);
        Interlocked.Add(ref sentBytesByKind[kind], packet.Length);
    }

    public void RecordQuotaRejected() => Interlocked.Increment(ref quotaRejectedPackets);

    public void RecordConnectionRejected() => Interlocked.Increment(ref rejectedConnections);

    public void RecordSnapshotDropped(int packetCount = 1)
    {
        if (packetCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packetCount));
        }

        Interlocked.Add(ref droppedSnapshotPackets, packetCount);
    }

    public void RecordSnapshotBackpressureDropped(int packetCount)
    {
        RecordSnapshotDropped(packetCount);
        Interlocked.Add(ref backpressureDroppedSnapshotPackets, packetCount);
    }

    public void RecordJoinAccepted() => Interlocked.Increment(ref acceptedJoins);

    public void RecordJoinRejected() => Interlocked.Increment(ref rejectedJoins);

    public void RecordSpawnPacket() => Interlocked.Increment(ref spawnPackets);

    public void RecordDespawnPacket() => Interlocked.Increment(ref despawnPackets);

    // The spawn and despawn packets since the worker started, which the tick loop reads around
    // each tick.
    internal long SpawnPackets => Volatile.Read(ref spawnPackets);

    internal long DespawnPackets => Volatile.Read(ref despawnPackets);

    public void RecordSnapshotEntities(int count) => Interlocked.Add(ref snapshotEntityRecords, count);

    // Payload bytes of snapshot packets handed to the transport, a subset of sent bytes.
    public void RecordSnapshotBytes(int bytes) => Interlocked.Add(ref snapshotBytes, bytes);

    public RealtimeNetworkMetricsSnapshot Capture()
    {
        var transport = Volatile.Read(ref datagrams);
        return new RealtimeNetworkMetricsSnapshot(
            Volatile.Read(ref activePeers),
            Volatile.Read(ref activeEntities),
            Volatile.Read(ref activeRealPlayers),
            Volatile.Read(ref activeSyntheticBots),
            Volatile.Read(ref unauthenticatedPeers),
            Volatile.Read(ref receivedPackets),
            Volatile.Read(ref receivedBytes),
            Volatile.Read(ref sentPackets),
            Volatile.Read(ref sentBytes),
            Volatile.Read(ref quotaRejectedPackets),
            Volatile.Read(ref droppedSnapshotPackets),
            Volatile.Read(ref backpressureDroppedSnapshotPackets),
            Volatile.Read(ref acceptedJoins),
            Volatile.Read(ref rejectedJoins),
            Volatile.Read(ref spawnPackets),
            Volatile.Read(ref despawnPackets),
            Volatile.Read(ref snapshotEntityRecords),
            Volatile.Read(ref snapshotBytes))
        {
            SentByMessage = CaptureKinds(sentMessagesByKind, sentBytesByKind),
            ReceivedByMessage = CaptureKinds(receivedMessagesByKind, receivedBytesByKind),
            DatagramsReceived = transport?.DatagramsReceived ?? 0,
            DatagramsSent = transport?.DatagramsSent ?? 0,
            DatagramBytesReceived = transport?.BytesReceived ?? 0,
            DatagramBytesSent = transport?.BytesSent ?? 0,
            SealingOverheadBytes = OverheadBytes(transport),
            DroppedDatagrams = transport?.Dropped ?? 0
        };
    }

    private static long OverheadBytes(RealtimeTransportSecurityCounters? counters)
    {
        return counters is null ? 0 : counters.OverheadBytesSent + counters.OverheadBytesReceived;
    }

    // Runs when a metrics reader collects: every reason with a dropped datagram.
    private IEnumerable<Measurement<long>> ObserveDrops()
    {
        var measurements = new List<Measurement<long>>();
        var counters = Volatile.Read(ref datagrams);
        if (counters is null)
        {
            return measurements;
        }

        foreach (var (reason, name) in DropReasons)
        {
            var count = counters.DroppedFor(reason);
            if (count > 0)
            {
                measurements.Add(new Measurement<long>(
                    count,
                    new KeyValuePair<string, object?>(DropReasonTagName, name)));
            }
        }

        return measurements;
    }

    private static RealtimeMessageKindTraffic[] CaptureKinds(long[] messages, long[] bytes)
    {
        var kinds = new List<RealtimeMessageKindTraffic>();
        for (var kind = 0; kind < messages.Length; kind++)
        {
            var count = Volatile.Read(ref messages[kind]);
            if (count > 0)
            {
                kinds.Add(new RealtimeMessageKindTraffic((byte)kind, count, Volatile.Read(ref bytes[kind])));
            }
        }

        return [.. kinds];
    }

    // Runs when a metrics reader collects, never on the loop thread's hot path.
    private static IEnumerable<Measurement<long>> ObserveKinds(long[] messages)
    {
        var measurements = new List<Measurement<long>>();
        for (var kind = 0; kind < messages.Length; kind++)
        {
            var count = Volatile.Read(ref messages[kind]);
            if (count > 0)
            {
                measurements.Add(new Measurement<long>(
                    count,
                    new KeyValuePair<string, object?>(MessageTagName, RealtimeMessageKinds.NameOf((byte)kind))));
            }
        }

        return measurements;
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
