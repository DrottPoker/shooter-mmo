using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed class StressCorpseViewState
{
    private readonly Dictionary<Guid, ChunkAssembly> assemblies = [];
    private readonly Dictionary<string, SectionState> sections = new(StringComparer.Ordinal);

    public Guid CorpseId { get; private set; }

    public long Revision { get; private set; } = -1;

    public bool IsReady => CorpseId != Guid.Empty && Revision >= 0;

    public CorpseItemLocation? FindLootStack(string definitionId, bool requireAvailableCapacity)
    {
        if (!sections.TryGetValue("general_inventory", out var section))
        {
            return null;
        }

        return section.Slots.Values
            .Where(slot => slot.Item is not null
                && string.Equals(slot.Item.DefinitionId, definitionId, StringComparison.Ordinal)
                && (!requireAvailableCapacity || slot.Item.Quantity < 100))
            .OrderBy(slot => slot.SlotIndex)
            .Select(slot => new CorpseItemLocation(
                section.ContainerId,
                section.ContainerRevision,
                slot.SlotIndex,
                slot.Item!))
            .FirstOrDefault();
    }

    public CorpseViewApplyResult Apply(RealtimeCorpseViewStateChunk chunk)
    {
        if (!assemblies.TryGetValue(chunk.UpdateId, out var assembly))
        {
            assembly = new ChunkAssembly(chunk);
            assemblies.Add(chunk.UpdateId, assembly);
        }

        var addResult = assembly.Add(chunk);
        if (addResult != CorpseViewApplyResult.Applied)
        {
            return addResult;
        }

        assemblies.Remove(chunk.UpdateId);
        var chunks = assembly.Chunks;
        if (chunk.UpdateKind == RealtimeCorpseViewUpdateKind.Snapshot)
        {
            if (IsReady && chunk.Revision < Revision)
            {
                return CorpseViewApplyResult.Stale;
            }

            sections.Clear();
        }
        else if (!IsReady
            || CorpseId != chunk.CorpseId
            || Revision != chunk.BaseRevision)
        {
            return CorpseViewApplyResult.Stale;
        }

        foreach (var group in chunks.GroupBy(candidate => candidate.SectionKind))
        {
            var updates = group.ToArray();
            var first = updates[0];
            if (first.ReplaceSection || !sections.TryGetValue(group.Key, out var section))
            {
                section = new SectionState(
                    first.ContainerId,
                    first.ContainerRevision,
                    first.SectionSlotCapacity,
                    []);
                sections[group.Key] = section;
            }

            if (section.ContainerId != first.ContainerId
                || updates.Any(update => update.ContainerRevision != first.ContainerRevision))
            {
                return CorpseViewApplyResult.Invalid;
            }

            section.ContainerRevision = first.ContainerRevision;
            foreach (var update in updates)
            {
                foreach (var slot in update.Slots)
                {
                    section.Slots[slot.SlotIndex] = new SlotState(
                        slot.SlotIndex,
                        slot.Item);
                }
            }
        }

        CorpseId = chunk.CorpseId;
        Revision = chunk.Revision;
        return CorpseViewApplyResult.Applied;
    }

    public void Clear()
    {
        assemblies.Clear();
        sections.Clear();
        CorpseId = Guid.Empty;
        Revision = -1;
    }

    public sealed record CorpseItemLocation(
        Guid ContainerId,
        long ContainerRevision,
        int SlotIndex,
        RealtimeCorpseItem Item);

    private sealed class ChunkAssembly(RealtimeCorpseViewStateChunk first)
    {
        private readonly Dictionary<ushort, RealtimeCorpseViewStateChunk> chunks = [];

        public IReadOnlyList<RealtimeCorpseViewStateChunk> Chunks => chunks
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();

        public CorpseViewApplyResult Add(RealtimeCorpseViewStateChunk chunk)
        {
            if (chunk.UpdateId != first.UpdateId
                || chunk.UpdateKind != first.UpdateKind
                || chunk.CorpseId != first.CorpseId
                || chunk.BaseRevision != first.BaseRevision
                || chunk.Revision != first.Revision
                || chunk.ChunkCount != first.ChunkCount
                || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                return CorpseViewApplyResult.Invalid;
            }

            if (!chunks.TryAdd(chunk.ChunkIndex, chunk))
            {
                return CorpseViewApplyResult.Duplicate;
            }

            return chunks.Count == first.ChunkCount
                ? CorpseViewApplyResult.Applied
                : CorpseViewApplyResult.Waiting;
        }
    }

    private sealed class SectionState(
        Guid containerId,
        long containerRevision,
        int slotCapacity,
        Dictionary<int, SlotState> slots)
    {
        public Guid ContainerId { get; } = containerId;

        public long ContainerRevision { get; set; } = containerRevision;

        public int SlotCapacity { get; } = slotCapacity;

        public Dictionary<int, SlotState> Slots { get; } = slots;
    }

    private sealed record SlotState(int SlotIndex, RealtimeCorpseItem? Item);
}

public enum CorpseViewApplyResult
{
    Waiting,
    Applied,
    Duplicate,
    Stale,
    Invalid
}
