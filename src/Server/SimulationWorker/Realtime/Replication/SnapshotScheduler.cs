using System.Runtime.CompilerServices;
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
// whether a due record was deferred, and the priority a waiting near change gathered. RecordSlot
// remembers where the entity's record was, checked against the entity before it is trusted. The
// entry fits 32 bytes, because planning reads one per visible entity of every recipient.
internal struct SnapshotRecipientEntity
{
    public ulong EntityId;
    public ulong SentVersion;
    public uint SentSequence;
    public int RecordSlot;
    public float NearPriority;
    public byte RepeatsLeft;
    public bool NewInView;
    public bool Deferred;
}

// The remote replication state of one recipient: what it got of each entity it sees, one entry
// per visible entity in the order of its visible list, ascending by id. It belongs to one
// connection and its controlled entity, and starts empty for another. Loop-confined.
internal sealed class SnapshotRecipientState(RealtimePeerContext context, ulong controlledEntityId)
{
    public RealtimePeerContext Context { get; } = context;

    public ulong ControlledEntityId { get; } = controlledEntityId;

    // The snapshot that last planned for this recipient.
    public uint LastSequence { get; set; }

    // The version of the visible list that the entries are parallel to; 0 for none.
    internal ulong AlignedVisibleVersion { get; set; }

    internal List<SnapshotRecipientEntity> Entities { get; private set; } = [];

    // Where the recipient's own record was, checked like an entry's.
    internal int OwnRecordSlot { get; set; } = -1;

    // The entries for another visible list, built beside the current ones and then swapped in,
    // so a change of the list costs one pass.
    internal List<SnapshotRecipientEntity> Spare { get; private set; } = [];

    internal void SwapSpare()
    {
        (Entities, Spare) = (Spare, Entities);
    }
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
// snapshot. A recipient's entries stay parallel to its visible list and remember where each
// record is, so a plan reads arrays and looks an entity up only when its view or its record
// moved. Nothing allocates once the buffers have grown. Loop-confined.
internal sealed class SnapshotScheduler
{
    private const int ClassCount = 6;

    // The near changes share one list, because their priority already holds how long they waited.
    private const int NearChangedIndex = ((int)SnapshotRecordClass.NearChanged * 2) + 1;

    // Closer than this, every near change gathers priority as one at this distance.
    private const float MinimumPriorityDistance = 1f;

    private readonly SnapshotReplicationConfig settings;

    // The intervals, and the multipliers that take an entity id modulo them without dividing.
    private readonly uint farInterval;
    private readonly uint refreshInterval;
    private readonly ulong farMultiplier;
    private readonly ulong refreshMultiplier;

    // Per class, the due records that were deferred before, then the others, and which of each
    // list the plan took: a count from a start, wrapping around.
    private readonly List<Due>[] due = new List<Due>[ClassCount * 2];
    private readonly int[] takenStarts = new int[ClassCount * 2];
    private readonly int[] takenCounts = new int[ClassCount * 2];
    private readonly List<Due> selected = [];
    private readonly byte[] packet = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];
    private SnapshotRecords? plannedRecords;
    private SnapshotRecipientState? plannedState;
    private int plannedPackets;
    private int plannedOwnSlot;
    private bool nearWeighted;

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
        farInterval = (uint)settings.FarIntervalSnapshots;
        refreshInterval = (uint)settings.RefreshIntervalSnapshots;
        farMultiplier = unchecked((ulong.MaxValue / farInterval) + 1);
        refreshMultiplier = unchecked((ulong.MaxValue / refreshInterval) + 1);
        for (var index = 0; index < due.Length; index++)
        {
            due[index] = [];
        }
    }

    // Chooses the records of one recipient within its byte budget, of which the owner snapshot
    // takes its share first. orderedVisible is ascending by id, and orderedNear tells for each of
    // its entities whether it is near. visibleVersion names the visible list, so an unchanged one
    // needs no check; 0 means unknown. The plan holds until Complete; WriteChunk builds its
    // packets.
    public SnapshotRecipientPlan Plan(
        SnapshotRecords records,
        SnapshotRecipientState state,
        ReadOnlySpan<ulong> orderedVisible,
        ReadOnlySpan<bool> orderedNear,
        int bytesPerRecipient,
        ulong visibleVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerRecipient);
        if (orderedNear.Length != orderedVisible.Length)
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

        Array.Clear(takenCounts);
        state.LastSequence = sequence;
        if (visibleVersion == 0
            || visibleVersion != state.AlignedVisibleVersion
            || state.Entities.Count != orderedVisible.Length)
        {
            Align(state, orderedVisible);
            state.AlignedVisibleVersion = visibleVersion;
        }

        // The id modulo the interval that puts an entity on its far or refresh slot in this snapshot.
        var farSlotPhase = SlotPhase(sequence, farInterval);
        var refreshSlotPhase = SlotPhase(sequence, refreshInterval);
        var controlledEntityId = state.ControlledEntityId;
        var ownSlot = state.OwnRecordSlot = ResolveRecordSlot(records, controlledEntityId, state.OwnRecordSlot);
        var recordEntityIds = records.EntityIds;
        var recordVersions = records.Versions;
        var entities = CollectionsMarshal.AsSpan(state.Entities);
        for (var index = 0; index < entities.Length; index++)
        {
            ref var entry = ref entities[index];
            var entityId = entry.EntityId;
            if (entityId == controlledEntityId)
            {
                continue;
            }

            // The remembered slot, while it still holds the entity's record.
            var slot = entry.RecordSlot;
            if ((uint)slot >= (uint)recordEntityIds.Length || recordEntityIds[slot] != entityId)
            {
                slot = entry.RecordSlot = records.TryGetSlot(entityId, out var current) ? current : -1;
                if (slot < 0)
                {
                    // Without a record the entity is new again when its record returns.
                    entry = NewlyVisible(entityId);
                    continue;
                }
            }

            if (!TryClassify(ref entry, recordVersions[slot], orderedNear[index], sequence, farSlotPhase, refreshSlotPhase, out var recordClass))
            {
                entry.NearPriority = 0f;
                continue;
            }

            // A near change gathers its priority only when not every near change goes out, since
            // a sent record starts again from zero.
            if (recordClass != SnapshotRecordClass.NearChanged)
            {
                entry.NearPriority = 0f;
            }

            due[DueIndex(recordClass, entry.Deferred)].Add(new Due(slot, index, recordClass, 0f));
        }

        plannedOwnSlot = ownSlot;
        nearWeighted = false;

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
            var list = due[dueIndex];
            var take = Math.Min(remaining, list.Count);
            var start = 0;
            if (take < list.Count)
            {
                if (dueIndex == NearChangedIndex)
                {
                    WeighNearChanges();
                    if (take > 0)
                    {
                        MoveHighestPriorityFirst(CollectionsMarshal.AsSpan(list), take);
                    }
                }
                else if (take > 0)
                {
                    // A part that cannot fit whole sends a subset that rotates with the snapshot.
                    start = (int)(((ulong)unchecked(sequence - 1u) * (uint)take) % (ulong)list.Count);
                }
            }

            takenStarts[dueIndex] = start;
            takenCounts[dueIndex] = take;
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
        foreach (var item in CollectionsMarshal.AsSpan(selected).Slice(first, recordCount))
        {
            records.CopyRecord(item.Slot, packet.AsSpan(length));
            length += SnapshotRecords.RecordSize;
        }

        return packet.AsSpan(0, length);
    }

    // Ends the plan. When its packets went out, the recipient has the selected records; every due
    // record that did not go out stays due in the next snapshot.
    public void Complete(bool sent)
    {
        var records = plannedRecords;
        var state = plannedState;
        if (records is null || state is null)
        {
            return;
        }

        // What did not go out is deferred, and the near changes among it gathered their priority.
        var entities = CollectionsMarshal.AsSpan(state.Entities);
        for (var dueIndex = 0; dueIndex < due.Length; dueIndex++)
        {
            var list = CollectionsMarshal.AsSpan(due[dueIndex]);
            var taken = sent ? takenCounts[dueIndex] : 0;
            if (taken == list.Length)
            {
                continue;
            }

            if (dueIndex == NearChangedIndex)
            {
                WeighNearChanges();
            }

            var start = takenStarts[dueIndex];
            for (var index = taken; index < list.Length; index++)
            {
                entities[list[(start + index) % list.Length].EntryIndex].Deferred = true;
            }
        }

        if (sent)
        {
            var versions = records.Versions;
            foreach (var item in CollectionsMarshal.AsSpan(selected))
            {
                // A change starts the stop repeats, and a repeat counts one off; the record of an
                // entity new in view confirms its spawn and starts none.
                ref var entry = ref entities[item.EntryIndex];
                var version = versions[item.Slot];
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

        plannedRecords = null;
        plannedState = null;
        selected.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryClassify(
        ref SnapshotRecipientEntity entry,
        ulong version,
        bool near,
        uint sequence,
        uint farSlotPhase,
        uint refreshSlotPhase,
        out SnapshotRecordClass recordClass)
    {
        if (entry.NewInView)
        {
            recordClass = SnapshotRecordClass.NewInView;
            return true;
        }

        bool isDue;
        if (version != entry.SentVersion)
        {
            recordClass = near ? SnapshotRecordClass.NearChanged : SnapshotRecordClass.FarChanged;
            isDue = near || entry.Deferred || Modulo(entry.EntityId, farMultiplier, farInterval) == farSlotPhase;
        }
        else if (entry.RepeatsLeft > 0)
        {
            recordClass = SnapshotRecordClass.StopRepeat;
            isDue = near || entry.Deferred || Modulo(entry.EntityId, farMultiplier, farInterval) == farSlotPhase;
        }
        else
        {
            recordClass = SnapshotRecordClass.Refresh;
            isDue = entry.Deferred || Modulo(entry.EntityId, refreshMultiplier, refreshInterval) == refreshSlotPhase;
        }

        // A refresh on time comes one interval after the last record; only a deferred one is late.
        if (isDue && unchecked(sequence - entry.SentSequence) > refreshInterval)
        {
            recordClass = SnapshotRecordClass.Overdue;
        }

        return isDue;
    }

    // Makes the entries parallel to the visible list. Both ascend by id, so a changed list is
    // merged in one pass: an entity that stayed keeps its entry, a new one gets a fresh entry,
    // and one that left the view is forgotten. An unchanged list costs one comparison per entity.
    private static void Align(SnapshotRecipientState state, ReadOnlySpan<ulong> orderedVisible)
    {
        var entries = CollectionsMarshal.AsSpan(state.Entities);
        if (entries.Length == orderedVisible.Length)
        {
            var index = 0;
            while (index < entries.Length && entries[index].EntityId == orderedVisible[index])
            {
                index++;
            }

            if (index == entries.Length)
            {
                return;
            }
        }

        var spare = state.Spare;
        CollectionsMarshal.SetCount(spare, orderedVisible.Length);
        var merged = CollectionsMarshal.AsSpan(spare);
        var entry = 0;
        for (var index = 0; index < orderedVisible.Length; index++)
        {
            var entityId = orderedVisible[index];
            if (index > 0 && entityId <= orderedVisible[index - 1])
            {
                throw new ArgumentException("The visible entities must ascend by id.", nameof(orderedVisible));
            }

            while (entry < entries.Length && entries[entry].EntityId < entityId)
            {
                entry++;
            }

            merged[index] = entry < entries.Length && entries[entry].EntityId == entityId
                ? entries[entry++]
                : NewlyVisible(entityId);
        }

        state.SwapSpare();
    }

    // The slot of the entity's record: the remembered one when it still holds the entity's
    // record, else looked up; -1 when the entity has no record in this snapshot.
    private static int ResolveRecordSlot(SnapshotRecords records, ulong entityId, int slot)
    {
        if ((uint)slot < (uint)records.Count && records.EntityIdOf(slot) == entityId)
        {
            return slot;
        }

        return records.TryGetSlot(entityId, out var current) ? current : -1;
    }

    private static int DueIndex(SnapshotRecordClass recordClass, bool deferred)
    {
        return ((int)recordClass * 2) + (deferred && recordClass != SnapshotRecordClass.NearChanged ? 0 : 1);
    }

    // Every waiting near change gathers the inverse of its distance, once per plan, and the due
    // list takes the priorities for the selection.
    private void WeighNearChanges()
    {
        if (nearWeighted)
        {
            return;
        }

        nearWeighted = true;
        var records = plannedRecords!;
        var entities = CollectionsMarshal.AsSpan(plannedState!.Entities);
        var near = CollectionsMarshal.AsSpan(due[NearChangedIndex]);
        for (var index = 0; index < near.Length; index++)
        {
            ref var item = ref near[index];
            var distance = plannedOwnSlot >= 0 ? MathF.Sqrt(records.DistanceSquared(item.Slot, plannedOwnSlot)) : 0f;
            ref var entry = ref entities[item.EntryIndex];
            entry.NearPriority += 1f / MathF.Max(distance, MinimumPriorityDistance);
            item = item with { Priority = entry.NearPriority };
        }
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

    // Slots are staggered by entity id, so not every entity is due in the same snapshot: an entity
    // is on its slot when the sequence plus its id is a multiple of the interval, so when its id
    // modulo the interval is this phase.
    private static uint SlotPhase(uint sequence, uint interval)
    {
        return (interval - (sequence % interval)) % interval;
    }

    // The id modulo the interval by a multiplication, exact for every 32-bit id, which every
    // entity with a record has.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Modulo(ulong entityId, ulong multiplier, uint interval)
    {
        return (uint)Math.BigMul(unchecked(multiplier * (uint)entityId), interval, out _);
    }

    private static SnapshotRecipientEntity NewlyVisible(ulong entityId)
    {
        return new SnapshotRecipientEntity { EntityId = entityId, RecordSlot = -1, NewInView = true };
    }

    private readonly record struct Due(int Slot, int EntryIndex, SnapshotRecordClass Class, float Priority);
}
