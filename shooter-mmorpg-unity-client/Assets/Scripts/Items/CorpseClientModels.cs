using System;
using System.Collections.Generic;
using System.Linq;
using ShooterMmo.GameProtocol;

namespace ShooterMmo.Items
{
    public enum CorpseClientStatus
    {
        Unavailable,
        Ready,
        Busy,
        Error
    }

    public enum CorpseStateApplyResult
    {
        Waiting,
        Applied,
        Duplicate,
        Stale,
        Invalid
    }

    public sealed class CorpseClientError
    {
        public CorpseClientError(string code, string message)
        {
            Code = string.IsNullOrWhiteSpace(code) ? "corpse_error" : code;
            Message = string.IsNullOrWhiteSpace(message)
                ? "The corpse interaction failed."
                : message;
        }

        public string Code { get; }

        public string Message { get; }

        public string ToDisplayMessage()
        {
            return Code + ": " + Message;
        }
    }

    public sealed class CorpsePresenceEntry
    {
        public CorpsePresenceEntry(RealtimeCorpsePresence source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            CorpseId = source.CorpseId;
            PositionX = source.PositionX;
            PositionY = source.PositionY;
            PositionZ = source.PositionZ;
            ExpiresAtUnixMilliseconds = source.ExpiresAtUnixMilliseconds;
            IsEmpty = source.IsEmpty;
            SourceDisplayName = source.SourceDisplayName ?? string.Empty;
            PresentationKey = source.PresentationKey ?? string.Empty;
        }

        public Guid CorpseId { get; }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        public long ExpiresAtUnixMilliseconds { get; }

        public bool IsEmpty { get; }

        public string SourceDisplayName { get; }

        public string PresentationKey { get; }
    }

    public sealed class CorpseLootItem
    {
        public CorpseLootItem(RealtimeCorpseItem source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            ItemInstanceId = source.ItemInstanceId;
            DefinitionId = source.DefinitionId ?? string.Empty;
            Quantity = source.Quantity;
            Revision = source.Revision;
            BagContentsContainerId = source.BagContentsContainerId;
            BagContentsRevision = source.BagContentsRevision;
        }

        public Guid ItemInstanceId { get; }

        public string DefinitionId { get; }

        public int Quantity { get; }

        public long Revision { get; }

        public Guid BagContentsContainerId { get; }

        public long BagContentsRevision { get; }

        public bool HasBagContents
        {
            get { return BagContentsContainerId != Guid.Empty; }
        }

        public InventoryItem ToInventoryItem()
        {
            return new InventoryItem(
                ItemInstanceId,
                DefinitionId,
                Quantity,
                Revision,
                Array.Empty<InventoryPolicy>());
        }
    }

    public sealed class CorpseLootSlot
    {
        public CorpseLootSlot(int slotIndex, string slotKind, CorpseLootItem item)
            : this(slotIndex, slotKind, Array.Empty<string>(), item)
        {
        }

        public CorpseLootSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            CorpseLootItem item)
        {
            SlotIndex = slotIndex;
            SlotKind = slotKind ?? string.Empty;
            AcceptedTags = acceptedTags ?? Array.Empty<string>();
            Item = item;
        }

        public int SlotIndex { get; }

        public string SlotKind { get; }

        public IReadOnlyList<string> AcceptedTags { get; }

        public CorpseLootItem Item { get; }
    }

    public sealed class CorpseLootSection
    {
        private readonly Dictionary<int, CorpseLootSlot> slotsByIndex;

        public CorpseLootSection(
            string sectionKind,
            Guid containerId,
            long containerRevision,
            int slotCapacity,
            IEnumerable<CorpseLootSlot> slots)
        {
            SectionKind = sectionKind ?? string.Empty;
            ContainerId = containerId;
            ContainerRevision = containerRevision;
            SlotCapacity = slotCapacity;
            Slots = (slots ?? Enumerable.Empty<CorpseLootSlot>())
                .OrderBy(slot => slot.SlotIndex)
                .ToArray();
            slotsByIndex = Slots.ToDictionary(slot => slot.SlotIndex);
        }

        public string SectionKind { get; }

        public Guid ContainerId { get; }

        public long ContainerRevision { get; }

        public int SlotCapacity { get; }

        public IReadOnlyList<CorpseLootSlot> Slots { get; }

        public bool TryGetSlot(int slotIndex, out CorpseLootSlot slot)
        {
            return slotsByIndex.TryGetValue(slotIndex, out slot);
        }
    }

    public sealed class CorpseLootView
    {
        private readonly Dictionary<string, CorpseLootSection> sectionsByKind;

        public CorpseLootView(
            Guid corpseId,
            long revision,
            long expiresAtUnixMilliseconds,
            string sourceDisplayName,
            string presentationKey,
            IEnumerable<CorpseLootSection> sections)
        {
            CorpseId = corpseId;
            Revision = revision;
            ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
            SourceDisplayName = sourceDisplayName ?? string.Empty;
            PresentationKey = presentationKey ?? string.Empty;
            Sections = (sections ?? Enumerable.Empty<CorpseLootSection>())
                .OrderBy(section => SectionOrder(section.SectionKind))
                .ThenBy(section => section.SectionKind, StringComparer.Ordinal)
                .ToArray();
            sectionsByKind = Sections.ToDictionary(
                section => section.SectionKind,
                StringComparer.Ordinal);
        }

        public Guid CorpseId { get; }

        public long Revision { get; }

        public long ExpiresAtUnixMilliseconds { get; }

        public string SourceDisplayName { get; }

        public string PresentationKey { get; }

        public IReadOnlyList<CorpseLootSection> Sections { get; }

        public bool TryGetSection(string sectionKind, out CorpseLootSection section)
        {
            return sectionsByKind.TryGetValue(sectionKind ?? string.Empty, out section);
        }

        public bool TryFindItem(
            Guid itemInstanceId,
            out CorpseLootItem item,
            out CorpseLootSection section,
            out CorpseLootSlot slot)
        {
            foreach (var candidateSection in Sections)
            {
                foreach (var candidateSlot in candidateSection.Slots)
                {
                    if (candidateSlot.Item != null
                        && candidateSlot.Item.ItemInstanceId == itemInstanceId)
                    {
                        item = candidateSlot.Item;
                        section = candidateSection;
                        slot = candidateSlot;
                        return true;
                    }
                }
            }

            item = null;
            section = null;
            slot = null;
            return false;
        }

        private static int SectionOrder(string sectionKind)
        {
            switch (sectionKind)
            {
                case "equipment":
                    return 0;
                case "general_inventory":
                    return 1;
                case "bag":
                    return 2;
                default:
                    return 3;
            }
        }
    }

    public sealed class CorpseClientState
    {
        private readonly Dictionary<ushort, RealtimeCorpsePresenceSnapshotChunk>
            presenceChunks = new Dictionary<ushort, RealtimeCorpsePresenceSnapshotChunk>();
        private readonly Dictionary<Guid, ViewAssembly> viewAssemblies =
            new Dictionary<Guid, ViewAssembly>();
        private bool hasPresenceSequence;
        private uint presenceSequence;
        private ushort presenceChunkCount;

        public event Action Changed;

        public CorpseClientStatus Status { get; private set; } = CorpseClientStatus.Unavailable;

        public IReadOnlyList<CorpsePresenceEntry> NearbyCorpses { get; private set; } =
            Array.Empty<CorpsePresenceEntry>();

        public CorpseLootView ActiveView { get; private set; }

        public CorpseClientError Error { get; private set; }

        public Guid PendingOperationId { get; private set; }

        public bool CanMutate
        {
            get
            {
                return Status == CorpseClientStatus.Ready
                    && ActiveView != null
                    && PendingOperationId == Guid.Empty;
            }
        }

        public void MarkAvailable()
        {
            Status = CorpseClientStatus.Ready;
            Error = null;
            RaiseChanged();
        }

        public void BeginOperation(Guid operationId)
        {
            if (operationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A corpse operation requires an operation id.",
                    nameof(operationId));
            }

            PendingOperationId = operationId;
            Status = CorpseClientStatus.Busy;
            Error = null;
            RaiseChanged();
        }

        public void CompleteOperation(Guid operationId)
        {
            if (PendingOperationId != operationId)
            {
                return;
            }

            PendingOperationId = Guid.Empty;
            Status = CorpseClientStatus.Ready;
            Error = null;
            RaiseChanged();
        }

        public void SetError(Guid operationId, CorpseClientError error)
        {
            if (PendingOperationId != Guid.Empty
                && operationId != Guid.Empty
                && PendingOperationId != operationId)
            {
                return;
            }

            PendingOperationId = Guid.Empty;
            Status = CorpseClientStatus.Error;
            Error = error;
            RaiseChanged();
        }

        public void CloseView(string code, string message)
        {
            ActiveView = null;
            PendingOperationId = Guid.Empty;
            Status = CorpseClientStatus.Ready;
            Error = string.IsNullOrWhiteSpace(code)
                ? null
                : new CorpseClientError(code, message);
            viewAssemblies.Clear();
            RaiseChanged();
        }

        public void Reset()
        {
            hasPresenceSequence = false;
            presenceSequence = 0;
            presenceChunkCount = 0;
            presenceChunks.Clear();
            viewAssemblies.Clear();
            NearbyCorpses = Array.Empty<CorpsePresenceEntry>();
            ActiveView = null;
            PendingOperationId = Guid.Empty;
            Status = CorpseClientStatus.Unavailable;
            Error = null;
            RaiseChanged();
        }

        public CorpseStateApplyResult ApplyPresenceChunk(
            RealtimeCorpsePresenceSnapshotChunk chunk)
        {
            if (chunk == null || chunk.ChunkCount == 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                return CorpseStateApplyResult.Invalid;
            }

            if (hasPresenceSequence && chunk.SnapshotSequence != presenceSequence)
            {
                if (!IsNewer(chunk.SnapshotSequence, presenceSequence))
                {
                    return CorpseStateApplyResult.Stale;
                }

                presenceChunks.Clear();
                presenceSequence = chunk.SnapshotSequence;
                presenceChunkCount = chunk.ChunkCount;
            }
            else if (!hasPresenceSequence)
            {
                hasPresenceSequence = true;
                presenceSequence = chunk.SnapshotSequence;
                presenceChunkCount = chunk.ChunkCount;
            }
            else if (presenceChunks.Count == 0)
            {
                return CorpseStateApplyResult.Duplicate;
            }

            if (chunk.ChunkCount != presenceChunkCount)
            {
                return CorpseStateApplyResult.Invalid;
            }

            if (presenceChunks.ContainsKey(chunk.ChunkIndex))
            {
                return CorpseStateApplyResult.Duplicate;
            }

            presenceChunks.Add(chunk.ChunkIndex, chunk);
            if (presenceChunks.Count != presenceChunkCount)
            {
                return CorpseStateApplyResult.Waiting;
            }

            var entries = new List<CorpsePresenceEntry>();
            var ids = new HashSet<Guid>();
            for (ushort index = 0; index < presenceChunkCount; index++)
            {
                if (!presenceChunks.TryGetValue(index, out var orderedChunk))
                {
                    return CorpseStateApplyResult.Invalid;
                }

                foreach (var source in orderedChunk.Corpses)
                {
                    if (!ids.Add(source.CorpseId))
                    {
                        return CorpseStateApplyResult.Invalid;
                    }

                    entries.Add(new CorpsePresenceEntry(source));
                }
            }

            NearbyCorpses = entries
                .OrderBy(entry => entry.CorpseId)
                .ToArray();
            presenceChunks.Clear();
            if (Status == CorpseClientStatus.Unavailable)
            {
                Status = CorpseClientStatus.Ready;
            }

            RaiseChanged();
            return CorpseStateApplyResult.Applied;
        }

        public CorpseStateApplyResult ApplyViewChunk(RealtimeCorpseViewStateChunk chunk)
        {
            if (chunk == null || chunk.ChunkCount == 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                return CorpseStateApplyResult.Invalid;
            }

            if (!viewAssemblies.TryGetValue(chunk.UpdateId, out var assembly))
            {
                assembly = new ViewAssembly(chunk);
                viewAssemblies.Add(chunk.UpdateId, assembly);
            }

            var addResult = assembly.TryAdd(chunk);
            if (addResult != CorpseStateApplyResult.Applied)
            {
                return addResult;
            }

            viewAssemblies.Remove(chunk.UpdateId);
            CorpseLootView next;
            if (chunk.UpdateKind == RealtimeCorpseViewUpdateKind.Snapshot)
            {
                if (!TryBuildSnapshot(assembly.Chunks, out next))
                {
                    return CorpseStateApplyResult.Invalid;
                }

                if (ActiveView != null
                    && ActiveView.CorpseId == next.CorpseId
                    && next.Revision < ActiveView.Revision)
                {
                    return CorpseStateApplyResult.Stale;
                }
            }
            else
            {
                if (ActiveView == null
                    || ActiveView.CorpseId != chunk.CorpseId
                    || ActiveView.Revision != chunk.BaseRevision)
                {
                    return CorpseStateApplyResult.Stale;
                }

                if (!TryApplyDelta(ActiveView, assembly.Chunks, out next))
                {
                    return CorpseStateApplyResult.Invalid;
                }
            }

            ActiveView = next;
            Status = CorpseClientStatus.Ready;
            Error = null;
            RaiseChanged();
            return CorpseStateApplyResult.Applied;
        }

        private static bool TryBuildSnapshot(
            IReadOnlyList<RealtimeCorpseViewStateChunk> chunks,
            out CorpseLootView view)
        {
            view = null;
            var sectionKinds = chunks
                .Select(candidate => candidate.SectionKind)
                .ToHashSet(StringComparer.Ordinal);
            if (!sectionKinds.SetEquals(new[]
                {
                    "general_inventory",
                    "equipment",
                    "bag"
                }))
            {
                return false;
            }

            var sections = new List<CorpseLootSection>();
            foreach (var group in chunks.GroupBy(candidate => candidate.SectionKind))
            {
                if (!TryBuildSection(group.ToArray(), true, out var section))
                {
                    return false;
                }

                sections.Add(section);
            }

            var first = chunks[0];
            view = new CorpseLootView(
                first.CorpseId,
                first.Revision,
                first.ExpiresAtUnixMilliseconds,
                first.SourceDisplayName,
                first.PresentationKey,
                sections);
            return true;
        }

        private static bool TryApplyDelta(
            CorpseLootView current,
            IReadOnlyList<RealtimeCorpseViewStateChunk> chunks,
            out CorpseLootView view)
        {
            view = null;
            var replacements = new Dictionary<string, CorpseLootSection>(StringComparer.Ordinal);
            foreach (var group in chunks.GroupBy(candidate => candidate.SectionKind))
            {
                var updates = group.ToArray();
                if (!current.TryGetSection(group.Key, out var existing))
                {
                    return false;
                }

                if (updates[0].ReplaceSection)
                {
                    if (!TryBuildSection(updates, true, out var replacement))
                    {
                        return false;
                    }

                    replacements.Add(group.Key, replacement);
                    continue;
                }

                if (updates.Any(candidate => candidate.ReplaceSection)
                    || updates.Any(candidate => candidate.ContainerId != existing.ContainerId)
                    || updates.Any(
                        candidate => candidate.ContainerRevision
                            != updates[0].ContainerRevision)
                    || updates.Any(
                        candidate => candidate.SectionSlotCapacity
                            != existing.SlotCapacity))
                {
                    return false;
                }

                var slots = existing.Slots.ToDictionary(slot => slot.SlotIndex);
                foreach (var update in updates)
                {
                    foreach (var sourceSlot in update.Slots)
                    {
                        if (!slots.ContainsKey(sourceSlot.SlotIndex))
                        {
                            return false;
                        }

                        slots[sourceSlot.SlotIndex] = MapSlot(sourceSlot);
                    }
                }

                replacements.Add(
                    group.Key,
                    new CorpseLootSection(
                        group.Key,
                        existing.ContainerId,
                        updates[0].ContainerRevision,
                        existing.SlotCapacity,
                        slots.Values));
            }

            var nextSections = current.Sections
                .Select(section => replacements.TryGetValue(section.SectionKind, out var replacement)
                    ? replacement
                    : section)
                .ToArray();
            var first = chunks[0];
            view = new CorpseLootView(
                current.CorpseId,
                first.Revision,
                first.ExpiresAtUnixMilliseconds,
                first.SourceDisplayName,
                first.PresentationKey,
                nextSections);
            return true;
        }

        private static bool TryBuildSection(
            IReadOnlyList<RealtimeCorpseViewStateChunk> chunks,
            bool requireReplace,
            out CorpseLootSection section)
        {
            section = null;
            var first = chunks[0];
            if (requireReplace && chunks.Any(candidate => !candidate.ReplaceSection))
            {
                return false;
            }

            var slots = new Dictionary<int, CorpseLootSlot>();
            foreach (var chunk in chunks)
            {
                if (chunk.ContainerId != first.ContainerId
                    || chunk.ContainerRevision != first.ContainerRevision
                    || chunk.SectionSlotCapacity != first.SectionSlotCapacity)
                {
                    return false;
                }

                foreach (var sourceSlot in chunk.Slots)
                {
                    if (sourceSlot.SlotIndex < 0
                        || sourceSlot.SlotIndex >= first.SectionSlotCapacity
                        || !slots.TryAdd(sourceSlot.SlotIndex, MapSlot(sourceSlot)))
                    {
                        return false;
                    }
                }
            }

            if (slots.Count != first.SectionSlotCapacity)
            {
                return false;
            }

            section = new CorpseLootSection(
                first.SectionKind,
                first.ContainerId,
                first.ContainerRevision,
                first.SectionSlotCapacity,
                slots.Values);
            return true;
        }

        private static CorpseLootSlot MapSlot(RealtimeCorpseSlot source)
        {
            return new CorpseLootSlot(
                source.SlotIndex,
                source.SlotKind,
                source.AcceptedTags,
                source.Item == null ? null : new CorpseLootItem(source.Item));
        }

        private static bool IsNewer(uint candidate, uint current)
        {
            return candidate != current && unchecked((int)(candidate - current)) > 0;
        }

        private void RaiseChanged()
        {
            Changed?.Invoke();
        }

        private sealed class ViewAssembly
        {
            private readonly Dictionary<ushort, RealtimeCorpseViewStateChunk> chunks =
                new Dictionary<ushort, RealtimeCorpseViewStateChunk>();
            private readonly RealtimeCorpseViewStateChunk first;

            public ViewAssembly(RealtimeCorpseViewStateChunk source)
            {
                first = source;
            }

            public IReadOnlyList<RealtimeCorpseViewStateChunk> Chunks
            {
                get
                {
                    return chunks
                        .OrderBy(pair => pair.Key)
                        .Select(pair => pair.Value)
                        .ToArray();
                }
            }

            public CorpseStateApplyResult TryAdd(RealtimeCorpseViewStateChunk chunk)
            {
                if (chunk.UpdateId != first.UpdateId
                    || chunk.UpdateKind != first.UpdateKind
                    || chunk.CorpseId != first.CorpseId
                    || chunk.BaseRevision != first.BaseRevision
                    || chunk.Revision != first.Revision
                    || chunk.ExpiresAtUnixMilliseconds != first.ExpiresAtUnixMilliseconds
                    || !string.Equals(
                        chunk.SourceDisplayName,
                        first.SourceDisplayName,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        chunk.PresentationKey,
                        first.PresentationKey,
                        StringComparison.Ordinal)
                    || chunk.ChunkCount != first.ChunkCount)
                {
                    return CorpseStateApplyResult.Invalid;
                }

                if (chunks.ContainsKey(chunk.ChunkIndex))
                {
                    return CorpseStateApplyResult.Duplicate;
                }

                chunks.Add(chunk.ChunkIndex, chunk);
                return chunks.Count == first.ChunkCount
                    ? CorpseStateApplyResult.Applied
                    : CorpseStateApplyResult.Waiting;
            }
        }
    }
}
