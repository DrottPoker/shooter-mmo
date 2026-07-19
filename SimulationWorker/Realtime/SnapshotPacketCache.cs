using ShooterMmo.GameProtocol;

namespace SimulationWorker.Realtime;

internal sealed record SnapshotPacketBatch(
    IReadOnlyList<byte[]> Packets,
    IReadOnlyList<int> EntityCounts,
    int TotalBytes,
    int EntityCount,
    int DeferredEntityCount)
{
    public static SnapshotPacketBatch Empty { get; } = new([], [], 0, 0, 0);
}

internal sealed class SnapshotPacketCache
{
    private readonly Dictionary<ulong, RealtimeEntitySnapshot> snapshotsByEntityId = [];
    private readonly Dictionary<SnapshotCacheKey, SnapshotPacketBatch> packetsByVisibility =
        new(SnapshotCacheKeyComparer.Instance);

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

    public bool TryGetSnapshot(ulong entityId, out RealtimeEntitySnapshot? snapshot)
    {
        return snapshotsByEntityId.TryGetValue(entityId, out snapshot);
    }

    public SnapshotPacketBatch GetOrCreate(
        IReadOnlyList<ulong> orderedVisibleEntityIds,
        uint snapshotSequence,
        uint serverTick,
        int bytesPerRecipient,
        int overloadTargetUtilizationBasisPoints)
    {
        ArgumentNullException.ThrowIfNull(orderedVisibleEntityIds);
        if (bytesPerRecipient < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerRecipient));
        }

        if (overloadTargetUtilizationBasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overloadTargetUtilizationBasisPoints));
        }

        var key = new SnapshotCacheKey(
            orderedVisibleEntityIds,
            bytesPerRecipient,
            overloadTargetUtilizationBasisPoints);
        if (packetsByVisibility.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = CreateBatch(
            orderedVisibleEntityIds,
            snapshotSequence,
            serverTick,
            bytesPerRecipient,
            overloadTargetUtilizationBasisPoints);
        packetsByVisibility.Add(key, created);
        EncodedPacketCount += created.Packets.Count;
        return created;
    }

    internal static int CalculateEncodedBytes(int entityCount)
    {
        if (entityCount <= 0)
        {
            return 0;
        }

        var packetCount = (int)Math.Ceiling(
            entityCount / (double)RealtimeProtocol.MaximumSnapshotEntitiesPerChunk);
        return checked(
            (entityCount * RealtimeProtocol.SimulationSnapshotEntitySize)
            + (packetCount * RealtimeProtocol.SimulationSnapshotPacketHeaderSize));
    }

    internal static int CalculateMaximumEntityCount(int byteBudget, int availableEntities)
    {
        if (byteBudget <= 0 || availableEntities <= 0)
        {
            return 0;
        }

        var count = 0;
        while (count < availableEntities
            && CalculateEncodedBytes(count + 1) <= byteBudget)
        {
            count++;
        }

        return count;
    }

    private SnapshotPacketBatch CreateBatch(
        IReadOnlyList<ulong> orderedVisibleEntityIds,
        uint snapshotSequence,
        uint serverTick,
        int bytesPerRecipient,
        int overloadTargetUtilizationBasisPoints)
    {
        var visibleSnapshotIds = new List<ulong>(orderedVisibleEntityIds.Count);
        for (var index = 0; index < orderedVisibleEntityIds.Count; index++)
        {
            var entityId = orderedVisibleEntityIds[index];
            if (snapshotsByEntityId.ContainsKey(entityId))
            {
                visibleSnapshotIds.Add(entityId);
            }
        }

        if (visibleSnapshotIds.Count == 0)
        {
            return SnapshotPacketBatch.Empty;
        }

        var targetBytes = (int)Math.Min(
            int.MaxValue,
            ((long)bytesPerRecipient * overloadTargetUtilizationBasisPoints) / 10_000L);
        var fullBatchBytes = CalculateEncodedBytes(visibleSnapshotIds.Count);
        int remoteByteBudget;
        if (fullBatchBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize
            <= targetBytes)
        {
            remoteByteBudget = fullBatchBytes;
        }
        else
        {
            remoteByteBudget = Math.Max(
                0,
                targetBytes - RealtimeProtocol.OwnerSimulationSnapshotPacketSize);
        }

        var selectedCount = CalculateMaximumEntityCount(
            remoteByteBudget,
            visibleSnapshotIds.Count);
        if (selectedCount == 0)
        {
            return new SnapshotPacketBatch(
                [],
                [],
                0,
                0,
                visibleSnapshotIds.Count);
        }

        var selectedSnapshots = new RealtimeEntitySnapshot[selectedCount];
        var start = selectedCount == visibleSnapshotIds.Count
            ? 0
            : (int)(((ulong)unchecked(snapshotSequence - 1u) * (uint)selectedCount)
                % (ulong)visibleSnapshotIds.Count);
        for (var index = 0; index < selectedCount; index++)
        {
            var entityId = visibleSnapshotIds[(start + index) % visibleSnapshotIds.Count];
            selectedSnapshots[index] = snapshotsByEntityId[entityId];
        }

        var chunkCount = checked((ushort)Math.Ceiling(
            selectedCount / (double)RealtimeProtocol.MaximumSnapshotEntitiesPerChunk));
        var packets = new byte[chunkCount][];
        var entityCounts = new int[chunkCount];
        var totalBytes = 0;
        for (ushort chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunkEntityCount = Math.Min(
                RealtimeProtocol.MaximumSnapshotEntitiesPerChunk,
                selectedCount - (chunkIndex * RealtimeProtocol.MaximumSnapshotEntitiesPerChunk));
            var chunkEntities = new RealtimeEntitySnapshot[chunkEntityCount];
            Array.Copy(
                selectedSnapshots,
                chunkIndex * RealtimeProtocol.MaximumSnapshotEntitiesPerChunk,
                chunkEntities,
                0,
                chunkEntityCount);
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

        return new SnapshotPacketBatch(
            packets,
            entityCounts,
            totalBytes,
            selectedCount,
            visibleSnapshotIds.Count - selectedCount);
    }

    private readonly record struct SnapshotCacheKey(
        IReadOnlyList<ulong> EntityIds,
        int BytesPerRecipient,
        int OverloadTargetUtilizationBasisPoints);

    private sealed class SnapshotCacheKeyComparer : IEqualityComparer<SnapshotCacheKey>
    {
        public static SnapshotCacheKeyComparer Instance { get; } = new();

        public bool Equals(SnapshotCacheKey left, SnapshotCacheKey right)
        {
            if (left.BytesPerRecipient != right.BytesPerRecipient
                || left.OverloadTargetUtilizationBasisPoints
                    != right.OverloadTargetUtilizationBasisPoints
                || left.EntityIds.Count != right.EntityIds.Count)
            {
                return false;
            }

            if (ReferenceEquals(left.EntityIds, right.EntityIds))
            {
                return true;
            }

            for (var index = 0; index < left.EntityIds.Count; index++)
            {
                if (left.EntityIds[index] != right.EntityIds[index])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(SnapshotCacheKey key)
        {
            var hash = new HashCode();
            hash.Add(key.BytesPerRecipient);
            hash.Add(key.OverloadTargetUtilizationBasisPoints);
            hash.Add(key.EntityIds.Count);
            for (var index = 0; index < key.EntityIds.Count; index++)
            {
                hash.Add(key.EntityIds[index]);
            }

            return hash.ToHashCode();
        }
    }
}
