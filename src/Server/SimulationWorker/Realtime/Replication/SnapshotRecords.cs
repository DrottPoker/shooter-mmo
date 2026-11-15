using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Realtime;

// What one recipient's remote snapshot carries: the records sent, the visible updates the byte
// budget deferred, and the chunk packets with their total size including the packet headers.
internal readonly record struct SnapshotRecipientPlan(int RecordCount, int DeferredCount, int PacketCount, int TotalBytes);

// The newest movement state of one entity that a snapshot replicates.
internal readonly record struct SnapshotEntityState(ulong EntityId, PlayerMovementState State);

// The remote entity records of one snapshot and the chunk packets built from them. Each entity's
// record is quantized once per snapshot into its slot of one flat buffer. A recipient's chunks are
// assembled by copying the records it sees into one reusable packet buffer, which the transport
// copies when it sends, so nothing on this path allocates once the buffers have grown to the
// population. Under a byte budget that cannot carry every visible record, a fair subset that
// rotates with the snapshot sequence is sent and the rest is deferred. Loop-confined.
internal sealed class SnapshotRecords
{
    private const int RecordSize = RealtimeProtocol.SimulationSnapshotEntitySize;
    private const int RecordsPerChunk = RealtimeProtocol.MaximumSnapshotEntitiesPerChunk;

    private readonly Dictionary<ulong, int> slotByEntityId = [];
    private readonly List<int> visibleSlots = [];
    private readonly byte[] packet = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];
    private byte[] records = new byte[256 * RecordSize];
    private int plannedRecords;
    private int plannedStart;
    private int plannedPackets;

    public int Count => slotByEntityId.Count;

    public void Reset()
    {
        slotByEntityId.Clear();
        visibleSlots.Clear();
        plannedRecords = 0;
        plannedPackets = 0;
    }

    // Quantizes the entity's state into its slot. False, with no record, for a state that is not
    // finite, which no client could show.
    public bool Add(ulong entityId, in PlayerMovementState state)
    {
        if (!RealtimeEntitySnapshot.TryQuantizeHorizontal(state.PositionX, out var quantizedX)
            || !RealtimeEntitySnapshot.TryQuantizeVertical(state.PositionY, out var quantizedY)
            || !RealtimeEntitySnapshot.TryQuantizeHorizontal(state.PositionZ, out var quantizedZ)
            || !RealtimeEntitySnapshot.TryQuantizeYaw(state.YawDegrees, out var yawStep))
        {
            return false;
        }

        if (slotByEntityId.ContainsKey(entityId))
        {
            throw new InvalidOperationException($"Entity {entityId} has a snapshot record already.");
        }

        var slot = slotByEntityId.Count;
        if ((slot + 1) * RecordSize > records.Length)
        {
            Array.Resize(ref records, records.Length * 2);
        }

        RealtimeProtocol.EncodeEntitySnapshot(
            records.AsSpan(slot * RecordSize, RecordSize),
            checked((uint)entityId),
            quantizedX,
            quantizedY,
            quantizedZ,
            yawStep,
            state.IsGrounded);
        slotByEntityId.Add(entityId, slot);
        return true;
    }

    // Chooses the records of one recipient within its byte budget, of which the owner snapshot
    // takes its share first. The plan holds until the next call; WriteChunk builds its packets.
    public SnapshotRecipientPlan Plan(
        IReadOnlyList<ulong> orderedVisibleEntityIds,
        uint snapshotSequence,
        int bytesPerRecipient,
        int overloadTargetUtilizationBasisPoints)
    {
        ArgumentNullException.ThrowIfNull(orderedVisibleEntityIds);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerRecipient);
        if (overloadTargetUtilizationBasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(overloadTargetUtilizationBasisPoints));
        }

        plannedRecords = 0;
        plannedPackets = 0;
        visibleSlots.Clear();
        for (var index = 0; index < orderedVisibleEntityIds.Count; index++)
        {
            if (slotByEntityId.TryGetValue(orderedVisibleEntityIds[index], out var slot))
            {
                visibleSlots.Add(slot);
            }
        }

        if (visibleSlots.Count == 0)
        {
            return default;
        }

        var targetBytes = (int)Math.Min(
            int.MaxValue,
            ((long)bytesPerRecipient * overloadTargetUtilizationBasisPoints) / 10_000L);
        var fullBytes = CalculateEncodedBytes(visibleSlots.Count);
        var remoteByteBudget = fullBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize <= targetBytes
            ? fullBytes
            : Math.Max(0, targetBytes - RealtimeProtocol.OwnerSimulationSnapshotPacketSize);
        var selected = CalculateMaximumEntityCount(remoteByteBudget, visibleSlots.Count);
        if (selected == 0)
        {
            return new SnapshotRecipientPlan(0, visibleSlots.Count, 0, 0);
        }

        plannedRecords = selected;
        plannedStart = selected == visibleSlots.Count
            ? 0
            : (int)(((ulong)unchecked(snapshotSequence - 1u) * (uint)selected) % (ulong)visibleSlots.Count);
        plannedPackets = (selected + RecordsPerChunk - 1) / RecordsPerChunk;
        return new SnapshotRecipientPlan(
            selected,
            visibleSlots.Count - selected,
            plannedPackets,
            CalculateEncodedBytes(selected));
    }

    // Builds one chunk of the last plan into the packet buffer. The span stays valid until the
    // next call, and the transport copies it when it sends.
    public ReadOnlySpan<byte> WriteChunk(int chunkIndex, uint snapshotSequence, uint serverTick, out int recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunkIndex, plannedPackets);
        var first = chunkIndex * RecordsPerChunk;
        recordCount = Math.Min(RecordsPerChunk, plannedRecords - first);
        var length = RealtimeProtocol.EncodeSimulationSnapshotHeader(
            packet,
            snapshotSequence,
            serverTick,
            (ushort)chunkIndex,
            (ushort)plannedPackets,
            recordCount);
        for (var index = 0; index < recordCount; index++)
        {
            var slot = visibleSlots[(plannedStart + first + index) % visibleSlots.Count];
            records.AsSpan(slot * RecordSize, RecordSize).CopyTo(packet.AsSpan(length));
            length += RecordSize;
        }

        return packet.AsSpan(0, length);
    }

    // The bytes of entityCount records in as few chunks as hold them, with their packet headers.
    internal static int CalculateEncodedBytes(int entityCount)
    {
        if (entityCount <= 0)
        {
            return 0;
        }

        var packetCount = (entityCount + RecordsPerChunk - 1) / RecordsPerChunk;
        return checked((entityCount * RecordSize) + (packetCount * RealtimeProtocol.SimulationSnapshotPacketHeaderSize));
    }

    // The most of availableEntities records whose packets fit byteBudget.
    internal static int CalculateMaximumEntityCount(int byteBudget, int availableEntities)
    {
        if (byteBudget <= 0 || availableEntities <= 0)
        {
            return 0;
        }

        var count = 0;
        while (count < availableEntities && CalculateEncodedBytes(count + 1) <= byteBudget)
        {
            count++;
        }

        return count;
    }
}
