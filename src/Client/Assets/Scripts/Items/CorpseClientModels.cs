using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // Whether the state can start an operation. A rejection or closure is reported through
    // CorpseClientState.Error or Notice and never blocks the view.
    public enum CorpseClientStatus
    {
        Unavailable,
        Ready,
        Busy
    }

    public enum CorpseStateApplyResult
    {
        Waiting,
        Applied,
        Duplicate,
        Stale,
        Invalid,

        // View state of a corpse the client neither views nor is opening.
        Ignored
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
            YawDegrees = source.YawDegrees;
            ExpiresAtUnixMilliseconds = source.ExpiresAtUnixMilliseconds;
            IsEmpty = source.IsEmpty;
            SourceDisplayName = source.SourceDisplayName ?? string.Empty;
            PresentationKey = source.PresentationKey ?? string.Empty;
        }

        public Guid CorpseId { get; }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        // The facing of whoever left the corpse, so the remains lie as the body fell.
        public float YawDegrees { get; }

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

        // An item a prediction shows in a corpse slot.
        public CorpseLootItem(
            Guid itemInstanceId,
            string definitionId,
            int quantity,
            long revision,
            Guid bagContentsContainerId,
            long bagContentsRevision)
        {
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId ?? string.Empty;
            Quantity = quantity;
            Revision = revision;
            BagContentsContainerId = bagContentsContainerId;
            BagContentsRevision = bagContentsRevision;
        }

        public CorpseLootItem WithQuantity(int quantity)
        {
            return new CorpseLootItem(
                ItemInstanceId,
                DefinitionId,
                quantity,
                Revision,
                BagContentsContainerId,
                BagContentsRevision);
        }

        public static CorpseLootItem FromInventoryItem(InventoryItem item, Guid bagContentsContainerId = default, long bagContentsRevision = 0)
        {
            return new CorpseLootItem(
                item.ItemInstanceId,
                item.DefinitionId,
                item.Quantity,
                item.Revision,
                bagContentsContainerId,
                bagContentsRevision);
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
            : this(slotIndex, slotKind, Array.Empty<string>(), string.Empty, item)
        {
        }

        public CorpseLootSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            CorpseLootItem item)
            : this(slotIndex, slotKind, acceptedTags, string.Empty, item)
        {
        }

        public CorpseLootSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            string equipmentSlotId,
            CorpseLootItem item)
        {
            SlotIndex = slotIndex;
            SlotKind = slotKind ?? string.Empty;
            AcceptedTags = acceptedTags ?? Array.Empty<string>();
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            Item = item;
        }

        public int SlotIndex { get; }

        public string SlotKind { get; }

        public IReadOnlyList<string> AcceptedTags { get; }

        public string EquipmentSlotId { get; }

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

        public bool BagHasContents(CorpseLootItem bag)
        {
            if (bag == null || bag.BagContentsContainerId == Guid.Empty)
            {
                return false;
            }

            return Sections.Any(section =>
                section.ContainerId == bag.BagContentsContainerId
                && section.Slots.Any(slot => slot.Item != null));
        }

        private static int SectionOrder(string sectionKind)
        {
            switch (sectionKind)
            {
                case CorpseSectionKindIds.Equipment:
                    return 0;
                case CorpseSectionKindIds.GeneralInventory:
                    return 1;
                case CorpseSectionKindIds.Bag:
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

        // The corpse whose view the client holds or has asked to open. View state and
        // closures of any other corpse, such as the late state of a closed view, are ignored.
        private Guid viewCorpseId;
        private RealtimeCorpseInteractionKind pendingOperationKind;
        private Guid pendingCorpseId;

        // True when the worker closed the view while the pending operation was in flight.
        private bool pendingOutlivedView;
        private IReadOnlyList<CorpseViewEdit> predictedEdits = Array.Empty<CorpseViewEdit>();
        private CorpseLootView shownView;
        private CorpseLootView shownViewSource;

        public event Action Changed;

        public CorpseClientStatus Status { get; private set; } = CorpseClientStatus.Unavailable;

        public IReadOnlyList<CorpsePresenceEntry> NearbyCorpses { get; private set; } =
            Array.Empty<CorpsePresenceEntry>();

        public CorpseLootView ActiveView { get; private set; }

        public CorpseClientError Error { get; private set; }

        // A neutral status line, such as why the last view ended as intended. Empty when
        // there is nothing to say, and never set together with Error.
        public string Notice { get; private set; } = string.Empty;

        public Guid PendingOperationId { get; private set; }

        // True while the pending operation is a refresh the client started on its own.
        public bool IsAutomaticRefreshPending { get; private set; }

        public bool CanMutate
        {
            get
            {
                return Status == CorpseClientStatus.Ready
                    && ActiveView != null
                    && PendingOperationId == Guid.Empty;
            }
        }

        // The view as the player sees it: the confirmed view with the corpse side of every
        // pending predicted operation applied.
        public CorpseLootView ShownView
        {
            get
            {
                if (ActiveView == null || predictedEdits.Count == 0)
                {
                    return ActiveView;
                }

                if (!ReferenceEquals(shownViewSource, ActiveView))
                {
                    shownView = CorpseViewEdit.Apply(ActiveView, predictedEdits);
                    shownViewSource = ActiveView;
                }

                return shownView;
            }
        }

        // Whether the player may make a corpse operation that the client shows at once; the
        // inventory queue decides when it is sent.
        public bool CanShowOperations
        {
            get { return Status != CorpseClientStatus.Unavailable && ActiveView != null; }
        }

        public void SetPredictedEdits(IReadOnlyList<CorpseViewEdit> edits)
        {
            predictedEdits = edits ?? Array.Empty<CorpseViewEdit>();
            shownViewSource = null;
            Changed?.Invoke();
        }

        // Starts an operation the player asked for. It replaces the last outcome, so Error
        // and Notice clear.
        public void BeginOperation(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId)
        {
            Begin(operationId, operationKind, corpseId, false);
        }

        // Starts a refresh the client asks for on its own, after a rejection that requires
        // one or a stale delta. It tells the player nothing new, so the last outcome, such
        // as the rejection that asked for it, stays visible through its success and its
        // snapshot. Its own rejection replaces Error like any rejection.
        public void BeginAutomaticRefresh(Guid operationId, Guid corpseId)
        {
            Begin(operationId, RealtimeCorpseInteractionKind.Refresh, corpseId, true);
        }

        private void Begin(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            bool automaticRefresh)
        {
            if (operationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A corpse operation requires an operation id.",
                    nameof(operationId));
            }

            if (corpseId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A corpse operation requires a corpse id.",
                    nameof(corpseId));
            }

            PendingOperationId = operationId;
            pendingOperationKind = operationKind;
            pendingCorpseId = corpseId;
            pendingOutlivedView = false;
            IsAutomaticRefreshPending = automaticRefresh;
            if (operationKind == RealtimeCorpseInteractionKind.Open)
            {
                viewCorpseId = corpseId;
            }

            Status = CorpseClientStatus.Busy;
            if (!automaticRefresh)
            {
                Error = null;
                Notice = string.Empty;
            }

            RaiseChanged();
        }

        // Settles the pending operation with its result. False when the result does not
        // answer the pending operation, which the caller treats as a protocol conflict.
        public bool TryComplete(RealtimeCorpseInteractionResult result, out string error)
        {
            if (result == null
                || PendingOperationId == Guid.Empty
                || result.OperationId != PendingOperationId
                || result.OperationKind != pendingOperationKind
                || result.CorpseId != pendingCorpseId)
            {
                error = "The corpse operation result did not match the pending operation.";
                return false;
            }

            error = string.Empty;
            var outlivedView = pendingOutlivedView;
            var automaticRefresh = IsAutomaticRefreshPending;
            ClearPendingOperation();
            if (outlivedView)
            {
                // The closure already explains how the view ended, so its notice or error
                // stays, whatever this late result says.
                Status = CorpseClientStatus.Ready;
            }
            else if (!result.Succeeded)
            {
                if (result.OperationKind == RealtimeCorpseInteractionKind.Open
                    && ActiveView == null)
                {
                    viewCorpseId = Guid.Empty;
                }

                // A rejection keeps an open view usable. Error only reports why the
                // operation failed, and the automatic refresh of a view the result marks
                // stale keeps it.
                Status = CorpseClientStatus.Ready;
                Error = new CorpseClientError(
                    result.Error?.Code ?? "corpse_operation_rejected",
                    result.Error?.Message ?? "The corpse operation was rejected.");
                Notice = string.Empty;
            }
            else if (result.OperationKind == RealtimeCorpseInteractionKind.Close)
            {
                // The viewer's own confirmed close ends the view without a message.
                EndView(null, string.Empty);
                return true;
            }
            else
            {
                Status = CorpseClientStatus.Ready;
                if (!automaticRefresh)
                {
                    Error = null;
                    Notice = string.Empty;
                }
            }

            RaiseChanged();
            return true;
        }

        // Ends the view for a closure the worker reported, and returns false for a closure
        // of any other corpse. An intended end, such as an emptied or expired corpse, leaves
        // a neutral notice instead of an error. A pending operation stays correlated,
        // because the worker still sends its result.
        public bool ApplyClosure(Guid corpseId, string code, string message)
        {
            if (viewCorpseId == Guid.Empty || corpseId != viewCorpseId)
            {
                return false;
            }

            var closure = CorpseViewClosureRule.Classify(code, message);
            pendingOutlivedView = PendingOperationId != Guid.Empty;
            EndView(closure.Error, closure.Notice);
            return true;
        }

        private void EndView(CorpseClientError error, string notice)
        {
            ActiveView = null;
            viewCorpseId = Guid.Empty;
            viewAssemblies.Clear();
            Status = PendingOperationId == Guid.Empty
                ? CorpseClientStatus.Ready
                : CorpseClientStatus.Busy;
            Error = error;
            Notice = notice;
            RaiseChanged();
        }

        private void ClearPendingOperation()
        {
            PendingOperationId = Guid.Empty;
            pendingOperationKind = default;
            pendingCorpseId = Guid.Empty;
            pendingOutlivedView = false;
            IsAutomaticRefreshPending = false;
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
            viewCorpseId = Guid.Empty;
            ClearPendingOperation();
            Status = CorpseClientStatus.Unavailable;
            Error = null;
            Notice = string.Empty;
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

            if (viewCorpseId == Guid.Empty || chunk.CorpseId != viewCorpseId)
            {
                return CorpseStateApplyResult.Ignored;
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

            // Error and Notice report the viewer's own last outcome, so another viewer's
            // change keeps a rejection visible until the next operation.
            ActiveView = next;
            Status = PendingOperationId == Guid.Empty
                ? CorpseClientStatus.Ready
                : CorpseClientStatus.Busy;
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
                    CorpseSectionKindIds.GeneralInventory,
                    CorpseSectionKindIds.Equipment,
                    CorpseSectionKindIds.Bag
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
                source.EquipmentSlotId,
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
