using ShooterMmo.GameProtocol;

namespace SimulationWorker.Realtime;

internal sealed record SnapshotPacketBatch(
    IReadOnlyList<byte[]> Packets,
    IReadOnlyList<int> EntityCounts,
    int TotalBytes)
{
    public static SnapshotPacketBatch Empty { get; } = new([], [], 0);
}

internal sealed class SnapshotPacketCache
{
    private readonly Dictionary<ulong, RealtimeEntitySnapshot> snapshotsByEntityId = [];
    private readonly Dictionary<IReadOnlyList<ulong>, SnapshotPacketBatch> packetsByVisibility =
        new(OrderedEntityIdListComparer.Instance);

    public int VisibilityGroupCount => packetsByVisibility.Count;

    public int EncodedPacketCount { get; private set; }

    public int EntitySnapshotCount => snapshotsByEntityId.Count;

    public void Reset()
    {
        snapshotsByEntityId.Clear();
        packetsByVisibility.Clear();
        EncodedPacketCount = 0;
    }

    public void Add(RealtimeEntitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshotsByEntityId.Add(snapshot.EntityId, snapshot);
    }

    public SnapshotPacketBatch GetOrCreate(
        IReadOnlyList<ulong> orderedVisibleEntityIds,
        uint snapshotSequence,
        uint serverTick)
    {
        ArgumentNullException.ThrowIfNull(orderedVisibleEntityIds);
        if (packetsByVisibility.TryGetValue(orderedVisibleEntityIds, out var existing))
        {
            return existing;
        }

        var created = CreateBatch(
            orderedVisibleEntityIds,
            snapshotSequence,
            serverTick);
        packetsByVisibility.Add(orderedVisibleEntityIds, created);
        EncodedPacketCount += created.Packets.Count;
        return created;
    }

    private SnapshotPacketBatch CreateBatch(
        IReadOnlyList<ulong> orderedVisibleEntityIds,
        uint snapshotSequence,
        uint serverTick)
    {
        var entityCount = 0;
        for (var index = 0; index < orderedVisibleEntityIds.Count; index++)
        {
            if (snapshotsByEntityId.ContainsKey(orderedVisibleEntityIds[index]))
            {
                entityCount++;
            }
        }

        if (entityCount == 0)
        {
            return SnapshotPacketBatch.Empty;
        }

        var chunkCount = checked((ushort)Math.Ceiling(
            entityCount / (double)RealtimeProtocol.MaximumSnapshotEntitiesPerChunk));
        var packets = new byte[chunkCount][];
        var entityCounts = new int[chunkCount];
        var totalBytes = 0;
        var visibleIndex = 0;
        for (ushort chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunkEntityCount = Math.Min(
                RealtimeProtocol.MaximumSnapshotEntitiesPerChunk,
                entityCount - (chunkIndex * RealtimeProtocol.MaximumSnapshotEntitiesPerChunk));
            var chunkEntities = new RealtimeEntitySnapshot[chunkEntityCount];
            var chunkEntityIndex = 0;
            while (visibleIndex < orderedVisibleEntityIds.Count
                && chunkEntityIndex < chunkEntities.Length)
            {
                var entityId = orderedVisibleEntityIds[visibleIndex++];
                if (snapshotsByEntityId.TryGetValue(entityId, out var snapshot))
                {
                    chunkEntities[chunkEntityIndex++] = snapshot;
                }
            }

            packets[chunkIndex] = RealtimeProtocol.EncodeSimulationSnapshot(
                new RealtimeSimulationSnapshot(
                    snapshotSequence,
                    serverTick,
                    chunkIndex,
                    chunkCount,
                    chunkEntities));
            entityCounts[chunkIndex] = chunkEntityCount;
            totalBytes += packets[chunkIndex].Length;
        }

        return new SnapshotPacketBatch(packets, entityCounts, totalBytes);
    }

    private sealed class OrderedEntityIdListComparer : IEqualityComparer<IReadOnlyList<ulong>>
    {
        public static OrderedEntityIdListComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<ulong>? left, IReadOnlyList<ulong>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<ulong> values)
        {
            var hash = new HashCode();
            hash.Add(values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                hash.Add(values[index]);
            }

            return hash.ToHashCode();
        }
    }
}
