using System.Runtime.InteropServices;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

// Why a record is due for a recipient, in the order the byte budget takes them.
internal enum SnapshotRecordClass : byte
{
    // The entity entered the recipient's interest; its record confirms the spawn.
    NewInView,

    // A due record of an entity that the recipient got nothing of for longer than the refresh
    // interval, because the budget deferred it.
    Overdue,

    // A near entity whose record differs from the one the recipient last got.
    NearChanged,

    // A record that stopped changing, repeated so that a lost packet leaves nothing walking on.
    StopRepeat,

    // A far entity whose record differs from the one the recipient last got, on its far slot.
    FarChanged,

    // An entity sent again at least once per refresh interval, on its refresh slot.
    Refresh
}

// What one recipient's remote snapshot carries: the records sent, the due records the byte budget
// deferred, and the chunk packets with their total size including the packet headers.
internal readonly record struct SnapshotRecipientPlan(int RecordCount, int DeferredCount, int PacketCount, int TotalBytes);

// What a recipient got of one visible entity: the version of the record it last got and the
// snapshot that carried it, the stop repeats still due, whether the entity is new in view,
// whether a due record was deferred, and the priority a waiting near change gathered.
internal struct SnapshotRecipientEntity
{
    public ulong EntityId;
    public ulong SentVersion;
    public uint SentSequence;
    public uint SeenSequence;
    public float NearPriority;
    public byte RepeatsLeft;
    public bool NewInView;
    public bool Deferred;
}

// The remote replication state of one recipient: what it got of each entity it sees. It belongs
// to one connection and its controlled entity, and starts empty for another. Loop-confined.
internal sealed class SnapshotRecipientState(RealtimePeerContext context, ulong controlledEntityId)
{
    public RealtimePeerContext Context { get; } = context;

    public ulong ControlledEntityId { get; } = controlledEntityId;

    // The snapshot that last planned for this recipient.
    public uint LastSequence { get; set; }

    internal Dictionary<ulong, int> IndexByEntityId { get; } = [];

    internal List<SnapshotRecipientEntity> Entities { get; } = [];
}

// Decides which records each recipient gets in a snapshot and builds its chunks. A record is due
// when the entity is new in view, when its record differs from the one the recipient last got (a
// near entity at once, a far one on its far slot, every FarIntervalSnapshots snapshots staggered
// by entity id), for StopRepeats tier slots after it stopped changing, and on its refresh slot,
// every RefreshIntervalSnapshots snapshots staggered by entity id. The recipient's byte budget
// takes the due records class by class in that order, after the new ones the overdue ones: the
// due records of entities the recipient got nothing of for longer than the refresh interval.
// Inside a class, the records deferred in the snapshot before go first, and a part that cannot
// fit whole sends a subset that rotates with the snapshot. The near changes instead go by the
// priority each gathers while it waits, the inverse of its distance per snapshot, so when they
// do not all fit the nearest go every snapshot and the others in turn, less often the farther
// they are, and none waits for ever. A due record that does not fit stays due in the next
// snapshot. Nothing allocates once the buffers have grown. Loop-confined.
internal sealed class SnapshotScheduler
{
    private const int ClassCount = 6;

    // Closer than this, every near change gathers priority as one at this distance.
    private const float MinimumPriorityDistance = 1f;

    private readonly SnapshotReplicationConfig settings;

    // Per class, the due records that were deferred before, then the others.
    private readonly List<Due>[] due = new List<Due>[ClassCount * 2];
    private readonly List<Due> selected = [];
    private readonly byte[] packet = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];
    private SnapshotRecords? plannedRecords;
    private SnapshotRecipientState? plannedState;
    private int plannedPackets;
    private int seenCount;

    public SnapshotScheduler(SnapshotReplicationConfig settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FarIntervalSnapshots <= 0
            || settings.StopRepeats <= 0
            || settings.StopRepeats > byte.MaxValue
            || settings.RefreshIntervalSnapshots <= 0
            || settings.OverloadTargetUtilizationBasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentException("Snapshot replication settings are invalid.", nameof(settings));
        }

        this.settings = settings;
        for (var index = 0; index < due.Length; index++)
        {
            due[index] = [];
        }
    }

    // Chooses the records of one recipient within its byte budget, of which the owner snapshot
    // takes its share first. orderedNear tells for each entity of orderedVisible whether it is
    // near. The plan holds until Complete; WriteChunk builds its packets.
    public SnapshotRecipientPlan Plan(
        SnapshotRecords records,
        SnapshotRecipientState state,
        IReadOnlyList<ulong> orderedVisible,
        IReadOnlyList<bool> orderedNear,
        int bytesPerRecipient)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(orderedVisible);
        ArgumentNullException.ThrowIfNull(orderedNear);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerRecipient);
        if (orderedNear.Count != orderedVisible.Count)
        {
            throw new ArgumentException("Every visible entity needs its tier.", nameof(orderedNear));
        }

        var sequence = records.Sequence;
        plannedRecords = records;
        plannedState = state;
        plannedPackets = 0;
        selected.Clear();
        foreach (var list in due)
        {
            list.Clear();
        }

        state.LastSequence = sequence;
        seenCount = 0;
        var hasOwnSlot = records.TryGetSlot(state.ControlledEntityId, out var ownSlot);
        var entities = CollectionsMarshal.AsSpan(state.Entities);
        for (var index = 0; index < orderedVisible.Count; index++)
        {
            var entityId = orderedVisible[index];
            if (entityId == state.ControlledEntityId || !records.TryGetSlot(entityId, out var slot))
            {
                continue;
            }

            if (!state.IndexByEntityId.TryGetValue(entityId, out var entryIndex))
            {
                entryIndex = state.Entities.Count;
                state.IndexByEntityId.Add(entityId, entryIndex);
                state.Entities.Add(new SnapshotRecipientEntity { EntityId = entityId, NewInView = true });
                entities = CollectionsMarshal.AsSpan(state.Entities);
            }

            ref var entry = ref entities[entryIndex];
            entry.SeenSequence = sequence;
            seenCount++;
            if (!TryClassify(ref entry, records.VersionOf(slot), orderedNear[index], sequence, out var recordClass))
            {
                entry.NearPriority = 0f;
                continue;
            }

            if (recordClass == SnapshotRecordClass.NearChanged)
            {
                var distance = hasOwnSlot ? MathF.Sqrt(records.DistanceSquared(slot, ownSlot)) : 0f;
                entry.NearPriority += 1f / MathF.Max(distance, MinimumPriorityDistance);
            }
            else
            {
                entry.NearPriority = 0f;
            }

            due[DueIndex(recordClass, entry.Deferred)].Add(new Due(slot, entryIndex, recordClass, entry.NearPriority));
        }

        var dueCount = 0;
        foreach (var list in due)
        {
            dueCount += list.Count;
        }

        if (dueCount == 0)
        {
            return default;
        }

        var targetBytes = (int)Math.Min(
            int.MaxValue,
            ((long)bytesPerRecipient * settings.OverloadTargetUtilizationBasisPoints) / 10_000L);
        var fullBytes = SnapshotRecords.CalculateEncodedBytes(dueCount);
        var remoteByteBudget = fullBytes + RealtimeProtocol.OwnerSimulationSnapshotPacketSize <= targetBytes
            ? fullBytes
            : Math.Max(0, targetBytes - RealtimeProtocol.OwnerSimulationSnapshotPacketSize);
        var remaining = SnapshotRecords.CalculateMaximumEntityCount(remoteByteBudget, dueCount);
        for (var dueIndex = 0; dueIndex < due.Length; dueIndex++)
        {
            if (remaining == 0)
            {
                break;
            }

            var list = due[dueIndex];
            var take = Math.Min(remaining, list.Count);
            var start = 0;
            if (take < list.Count)
            {
                if (dueIndex == DueIndex(SnapshotRecordClass.NearChanged, deferred: false))
                {
                    MoveHighestPriorityFirst(CollectionsMarshal.AsSpan(list), take);
                }
                else
                {
                    // A part that cannot fit whole sends a subset that rotates with the snapshot.
                    start = (int)(((ulong)unchecked(sequence - 1u) * (uint)take) % (ulong)list.Count);
                }
            }

            for (var index = 0; index < take; index++)
            {
                selected.Add(list[(start + index) % list.Count]);
            }

            remaining -= take;
        }

        plannedPackets = (selected.Count + SnapshotRecords.RecordsPerChunk - 1) / SnapshotRecords.RecordsPerChunk;
        return new SnapshotRecipientPlan(
            selected.Count,
            dueCount - selected.Count,
            plannedPackets,
            SnapshotRecords.CalculateEncodedBytes(selected.Count));
    }

    // Builds one chunk of the plan into the packet buffer. The span stays valid until the next
    // call, and the transport copies it when it sends.
    public ReadOnlySpan<byte> WriteChunk(int chunkIndex, uint serverTick, out int recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunkIndex, plannedPackets);
        var records = plannedRecords!;
        var first = chunkIndex * SnapshotRecords.RecordsPerChunk;
        recordCount = Math.Min(SnapshotRecords.RecordsPerChunk, selected.Count - first);
        var length = RealtimeProtocol.EncodeSimulationSnapshotHeader(
            packet,
            records.Sequence,
            serverTick,
            (ushort)chunkIndex,
            (ushort)plannedPackets,
            recordCount);
        for (var index = 0; index < recordCount; index++)
        {
            records.Record(selected[first + index].Slot).CopyTo(packet.AsSpan(length));
            length += SnapshotRecords.RecordSize;
        }

        return packet.AsSpan(0, length);
    }

    // Ends the plan. When its packets went out, the recipient has the selected records; every due
    // record that did not go out stays due in the next snapshot. Then the entities that the
    // recipient no longer sees are forgotten.
    public void Complete(bool sent)
    {
        var records = plannedRecords;
        var state = plannedState;
        if (records is null || state is null)
        {
            return;
        }

        var entities = CollectionsMarshal.AsSpan(state.Entities);
        foreach (var list in due)
        {
            foreach (var item in list)
            {
                entities[item.EntryIndex].Deferred = true;
            }
        }

        if (sent)
        {
            foreach (var item in selected)
            {
                // A change starts the stop repeats, and a repeat counts one off; the record of an
                // entity new in view confirms its spawn and starts none.
                ref var entry = ref entities[item.EntryIndex];
                var version = records.VersionOf(item.Slot);
                if (item.Class == SnapshotRecordClass.NewInView)
                {
                    entry.RepeatsLeft = 0;
                }
                else if (version != entry.SentVersion)
                {
                    entry.RepeatsLeft = (byte)settings.StopRepeats;
                }
                else if (entry.RepeatsLeft > 0)
                {
                    entry.RepeatsLeft--;
                }

                entry.SentVersion = version;
                entry.SentSequence = records.Sequence;
                entry.NearPriority = 0f;
                entry.NewInView = false;
                entry.Deferred = false;
            }
        }

        if (state.Entities.Count > seenCount)
        {
            Forget(state, records.Sequence);
        }

        plannedRecords = null;
        plannedState = null;
        selected.Clear();
    }

    private bool TryClassify(
        ref SnapshotRecipientEntity entry,
        ulong version,
        bool near,
        uint sequence,
        out SnapshotRecordClass recordClass)
    {
        if (entry.NewInView)
        {
            recordClass = SnapshotRecordClass.NewInView;
            return true;
        }

        var tierSlot = near || entry.Deferred || IsSlot(sequence, entry.EntityId, settings.FarIntervalSnapshots);
        bool isDue;
        if (version != entry.SentVersion)
        {
            recordClass = near ? SnapshotRecordClass.NearChanged : SnapshotRecordClass.FarChanged;
            isDue = tierSlot;
        }
        else if (entry.RepeatsLeft > 0)
        {
            recordClass = SnapshotRecordClass.StopRepeat;
            isDue = tierSlot;
        }
        else
        {
            recordClass = SnapshotRecordClass.Refresh;
            isDue = entry.Deferred || IsSlot(sequence, entry.EntityId, settings.RefreshIntervalSnapshots);
        }

        // A refresh on time comes one interval after the last record; only a deferred one is late.
        if (isDue && unchecked(sequence - entry.SentSequence) > (uint)settings.RefreshIntervalSnapshots)
        {
            recordClass = SnapshotRecordClass.Overdue;
        }

        return isDue;
    }

    // The near changes share one list, because their priority already holds how long they waited.
    private static int DueIndex(SnapshotRecordClass recordClass, bool deferred)
    {
        return ((int)recordClass * 2) + (deferred && recordClass != SnapshotRecordClass.NearChanged ? 0 : 1);
    }

    // Moves the count items of highest priority to the front, in no particular order, by
    // partitioning around a median of three as quickselect does.
    private static void MoveHighestPriorityFirst(Span<Due> items, int count)
    {
        var target = count - 1;
        var left = 0;
        var right = items.Length - 1;
        while (left < right)
        {
            var middle = left + ((right - left) / 2);
            if (Before(items[middle], items[left]))
            {
                (items[middle], items[left]) = (items[left], items[middle]);
            }

            if (Before(items[right], items[left]))
            {
                (items[right], items[left]) = (items[left], items[right]);
            }

            if (Before(items[middle], items[right]))
            {
                (items[middle], items[right]) = (items[right], items[middle]);
            }

            // The median of the three is now at the right end and is the pivot.
            var pivot = items[right];
            var store = left;
            for (var index = left; index < right; index++)
            {
                if (Before(items[index], pivot))
                {
                    (items[index], items[store]) = (items[store], items[index]);
                    store++;
                }
            }

            (items[store], items[right]) = (items[right], items[store]);
            if (store == target)
            {
                return;
            }

            if (store > target)
            {
                right = store - 1;
            }
            else
            {
                left = store + 1;
            }
        }
    }

    // Higher priority first, and by slot between equal priorities, so the order is the same on
    // every run.
    private static bool Before(in Due first, in Due second)
    {
        return first.Priority != second.Priority ? first.Priority > second.Priority : first.Slot < second.Slot;
    }

    // Slots are staggered by entity id, so not every entity is due in the same snapshot.
    private static bool IsSlot(uint sequence, ulong entityId, int interval)
    {
        return (sequence + entityId) % (ulong)interval == 0;
    }

    private static void Forget(SnapshotRecipientState state, uint sequence)
    {
        var entities = CollectionsMarshal.AsSpan(state.Entities);
        for (var index = entities.Length - 1; index >= 0; index--)
        {
            if (entities[index].SeenSequence == sequence)
            {
                continue;
            }

            state.IndexByEntityId.Remove(entities[index].EntityId);
            var last = entities.Length - 1;
            if (index != last)
            {
                entities[index] = entities[last];
                state.IndexByEntityId[entities[index].EntityId] = index;
            }

            state.Entities.RemoveAt(last);
            entities = CollectionsMarshal.AsSpan(state.Entities);
        }
    }

    private readonly record struct Due(int Slot, int EntryIndex, SnapshotRecordClass Class, float Priority);
}
