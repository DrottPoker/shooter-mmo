using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Realtime;

// The newest movement state of one entity that a snapshot replicates.
internal readonly record struct SnapshotEntityState(ulong EntityId, PlayerMovementState State);

// The quantized remote record of every entity with a state, kept from snapshot to snapshot. Each
// snapshot reports every such entity once: its record is quantized into its slot of one flat
// buffer, and when the record differs from the one before, the slot gets a new version, a number
// that no other record of any entity ever had. A quantized position that changes after it did not
// change in the snapshot before is a start: the entity stood there, as a client judges by the
// position alone, and moves again. A start lasts StartSnapshots snapshots. The reported planar
// position is kept beside it for the distances of the budget order. An entity that a snapshot
// does not report loses its slot. Nothing here allocates once the buffers have grown to the
// population. Loop-confined.
internal sealed class SnapshotRecords
{
    internal const int RecordSize = RealtimeProtocol.SimulationSnapshotEntitySize;
    internal const int RecordsPerChunk = RealtimeProtocol.MaximumSnapshotEntitiesPerChunk;

    // The snapshots a start lasts: the one where the entity moved off, whose record follows the
    // start by at most one snapshot interval, and the next, whose record gives its speed.
    internal const int StartSnapshots = 2;

    private readonly Dictionary<ulong, int> slotByEntityId = [];
    private byte[] records = new byte[256 * RecordSize];
    private ulong[] entityIds = new ulong[256];
    private ulong[] versions = new ulong[256];
    private uint[] reportedSequences = new uint[256];

    // The quantized position of each record in one number, the snapshot that last moved it, and
    // the snapshots its start still lasts.
    private ulong[] quantizedPositions = new ulong[256];
    private uint[] movedSequences = new uint[256];
    private byte[] startSnapshotsLeft = new byte[256];
    private bool[] starts = new bool[256];
    private float[] positionsX = new float[256];
    private float[] positionsZ = new float[256];
    private int count;
    private ulong nextVersion;
    private uint sequence;

    public int Count => count;

    // The entity of every slot and the version of its record, for the planner's loop.
    internal ReadOnlySpan<ulong> EntityIds => entityIds.AsSpan(0, count);

    internal ReadOnlySpan<ulong> Versions => versions.AsSpan(0, count);

    // Whether this snapshot is one of the StartSnapshots of the slot's start: the snapshot where
    // its quantized position changed after it did not change in the snapshot before, or one after
    // it. False for a slot that is new in this snapshot.
    internal ReadOnlySpan<bool> Starts => starts.AsSpan(0, count);

    // The snapshot whose reports follow.
    public uint Sequence => sequence;

    public void BeginSnapshot(uint snapshotSequence)
    {
        sequence = snapshotSequence;
    }

    // Quantizes the entity's state into its slot. False, with no record, for a state that is not
    // finite, which no client could show.
    public bool Report(ulong entityId, in PlayerMovementState state)
    {
        if (!RealtimeEntitySnapshot.TryQuantizeHorizontal(state.PositionX, out var quantizedX)
            || !RealtimeEntitySnapshot.TryQuantizeVertical(state.PositionY, out var quantizedY)
            || !RealtimeEntitySnapshot.TryQuantizeHorizontal(state.PositionZ, out var quantizedZ)
            || !RealtimeEntitySnapshot.TryQuantizeYaw(state.YawDegrees, out var yawStep))
        {
            return false;
        }

        Span<byte> record = stackalloc byte[RecordSize];
        RealtimeProtocol.EncodeEntitySnapshot(
            record,
            checked((uint)entityId),
            quantizedX,
            quantizedY,
            quantizedZ,
            yawStep,
            state.IsGrounded);
        var position = PackPosition(quantizedX, quantizedY, quantizedZ);
        if (!slotByEntityId.TryGetValue(entityId, out var slot))
        {
            slot = count;
            EnsureCapacity(slot + 1);
            count++;
            slotByEntityId.Add(entityId, slot);
            entityIds[slot] = entityId;
            Store(slot, record);
            quantizedPositions[slot] = position;
            movedSequences[slot] = sequence;
            startSnapshotsLeft[slot] = 0;
            starts[slot] = false;
        }
        else if (reportedSequences[slot] == sequence)
        {
            throw new InvalidOperationException($"Entity {entityId} has a snapshot record already.");
        }
        else
        {
            if (!record.SequenceEqual(Record(slot)))
            {
                Store(slot, record);
            }

            var moved = position != quantizedPositions[slot];
            if (moved && movedSequences[slot] != unchecked(sequence - 1u))
            {
                startSnapshotsLeft[slot] = StartSnapshots;
            }

            if (moved)
            {
                quantizedPositions[slot] = position;
                movedSequences[slot] = sequence;
            }

            starts[slot] = startSnapshotsLeft[slot] > 0;
            if (starts[slot])
            {
                startSnapshotsLeft[slot]--;
            }
        }

        reportedSequences[slot] = sequence;
        positionsX[slot] = state.PositionX;
        positionsZ[slot] = state.PositionZ;
        return true;
    }

    // Drops the slot of every entity that this snapshot did not report.
    public void EndReports()
    {
        for (var slot = count - 1; slot >= 0; slot--)
        {
            if (reportedSequences[slot] == sequence)
            {
                continue;
            }

            slotByEntityId.Remove(entityIds[slot]);
            var last = count - 1;
            if (slot != last)
            {
                Record(last).CopyTo(records.AsSpan(slot * RecordSize, RecordSize));
                entityIds[slot] = entityIds[last];
                versions[slot] = versions[last];
                reportedSequences[slot] = reportedSequences[last];
                quantizedPositions[slot] = quantizedPositions[last];
                movedSequences[slot] = movedSequences[last];
                startSnapshotsLeft[slot] = startSnapshotsLeft[last];
                starts[slot] = starts[last];
                positionsX[slot] = positionsX[last];
                positionsZ[slot] = positionsZ[last];
                slotByEntityId[entityIds[slot]] = slot;
            }

            count--;
        }
    }

    public bool TryGetSlot(ulong entityId, out int slot)
    {
        return slotByEntityId.TryGetValue(entityId, out slot);
    }

    public ulong EntityIdOf(int slot)
    {
        return entityIds[slot];
    }

    // The version of the slot's record; a recipient that has this version has the record.
    public ulong VersionOf(int slot)
    {
        return versions[slot];
    }

    public ReadOnlySpan<byte> Record(int slot)
    {
        return records.AsSpan(slot * RecordSize, RecordSize);
    }

    // Copies the slot's record to the start of destination in one block move.
    internal void CopyRecord(int slot, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)count, nameof(slot));
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, RecordSize, nameof(destination));
        Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetReference(destination), ref records[slot * RecordSize], RecordSize);
    }

    // The squared planar distance between the reported positions of two slots.
    public float DistanceSquared(int slot, int otherSlot)
    {
        var deltaX = positionsX[slot] - positionsX[otherSlot];
        var deltaZ = positionsZ[slot] - positionsZ[otherSlot];
        return (deltaX * deltaX) + (deltaZ * deltaZ);
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

    // The most of availableEntities records whose packets fit byteBudget: as many full chunks as
    // fit, and in the rest one more header with as many records as fit after it.
    internal static int CalculateMaximumEntityCount(int byteBudget, int availableEntities)
    {
        if (byteBudget <= 0 || availableEntities <= 0)
        {
            return 0;
        }

        const int FullChunkBytes = (RecordsPerChunk * RecordSize) + RealtimeProtocol.SimulationSnapshotPacketHeaderSize;
        var fullChunks = byteBudget / FullChunkBytes;
        var rest = byteBudget - (fullChunks * FullChunkBytes);
        var partial = rest >= RealtimeProtocol.SimulationSnapshotPacketHeaderSize + RecordSize
            ? (rest - RealtimeProtocol.SimulationSnapshotPacketHeaderSize) / RecordSize
            : 0;
        return (int)Math.Min(availableEntities, ((long)fullChunks * RecordsPerChunk) + partial);
    }

    private void Store(int slot, ReadOnlySpan<byte> record)
    {
        record.CopyTo(records.AsSpan(slot * RecordSize, RecordSize));
        versions[slot] = ++nextVersion;
    }

    // The 24-bit x, 16-bit y, and 24-bit z steps of a record in one number.
    private static ulong PackPosition(int quantizedX, short quantizedY, int quantizedZ)
    {
        return ((ulong)((uint)quantizedX & 0xFF_FFFFu) << 40)
            | ((ulong)(ushort)quantizedY << 24)
            | ((uint)quantizedZ & 0xFF_FFFFu);
    }

    private void EnsureCapacity(int slots)
    {
        if (slots <= entityIds.Length)
        {
            return;
        }

        var capacity = entityIds.Length * 2;
        Array.Resize(ref records, capacity * RecordSize);
        Array.Resize(ref entityIds, capacity);
        Array.Resize(ref versions, capacity);
        Array.Resize(ref reportedSequences, capacity);
        Array.Resize(ref quantizedPositions, capacity);
        Array.Resize(ref movedSequences, capacity);
        Array.Resize(ref startSnapshotsLeft, capacity);
        Array.Resize(ref starts, capacity);
        Array.Resize(ref positionsX, capacity);
        Array.Resize(ref positionsZ, capacity);
    }
}
