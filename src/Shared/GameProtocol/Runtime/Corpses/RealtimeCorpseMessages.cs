using System;

namespace Mmorpg.GameProtocol
{
    public enum RealtimeCorpseInteractionKind : byte
    {
        Open = 1,
        Close = 2,
        Refresh = 3,
        LootItem = 4,
        LootPartialStack = 5,
        SwapBag = 6,
        DepositItem = 7,
        DepositPartialStack = 8,
        MoveItem = 9,
        MovePartialStack = 10
    }

    public enum RealtimeCorpseViewUpdateKind : byte
    {
        Snapshot = 1,
        Delta = 2
    }

    public sealed class RealtimeCorpsePresence
    {
        public RealtimeCorpsePresence(
            Guid corpseId,
            float positionX,
            float positionY,
            float positionZ,
            long expiresAtUnixMilliseconds,
            bool isEmpty,
            string sourceDisplayName,
            string presentationKey)
        {
            CorpseId = corpseId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
            IsEmpty = isEmpty;
            SourceDisplayName = sourceDisplayName;
            PresentationKey = presentationKey;
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

    public sealed class RealtimeCorpsePresenceSnapshotChunk
    {
        public RealtimeCorpsePresenceSnapshotChunk(
            uint snapshotSequence,
            ushort chunkIndex,
            ushort chunkCount,
            RealtimeCorpsePresence[] corpses)
        {
            SnapshotSequence = snapshotSequence;
            ChunkIndex = chunkIndex;
            ChunkCount = chunkCount;
            Corpses = corpses ?? Array.Empty<RealtimeCorpsePresence>();
        }

        public uint SnapshotSequence { get; }

        public ushort ChunkIndex { get; }

        public ushort ChunkCount { get; }

        public RealtimeCorpsePresence[] Corpses { get; }
    }

    public sealed class RealtimeCorpseInteractionIntent
    {
        private RealtimeCorpseInteractionIntent(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision,
            Guid corpseBagContentsContainerId,
            long expectedCorpseBagContentsRevision,
            Guid playerBagItemInstanceId,
            long expectedPlayerBagRevision,
            Guid playerBagContentsContainerId,
            long expectedPlayerBagContentsRevision)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            CorpseId = corpseId;
            ExpectedCorpseRevision = expectedCorpseRevision;
            ItemInstanceId = itemInstanceId;
            ExpectedItemRevision = expectedItemRevision;
            Quantity = quantity;
            DestinationContainerId = destinationContainerId;
            ExpectedDestinationContainerRevision = expectedDestinationContainerRevision;
            DestinationSlotIndex = destinationSlotIndex;
            TargetItemInstanceId = targetItemInstanceId;
            ExpectedTargetItemRevision = expectedTargetItemRevision;
            CorpseBagContentsContainerId = corpseBagContentsContainerId;
            ExpectedCorpseBagContentsRevision = expectedCorpseBagContentsRevision;
            PlayerBagItemInstanceId = playerBagItemInstanceId;
            ExpectedPlayerBagRevision = expectedPlayerBagRevision;
            PlayerBagContentsContainerId = playerBagContentsContainerId;
            ExpectedPlayerBagContentsRevision = expectedPlayerBagContentsRevision;
        }

        public Guid OperationId { get; }

        public RealtimeCorpseInteractionKind OperationKind { get; }

        public Guid CorpseId { get; }

        public long ExpectedCorpseRevision { get; }

        public Guid ItemInstanceId { get; }

        public long ExpectedItemRevision { get; }

        public int Quantity { get; }

        public Guid DestinationContainerId { get; }

        public long ExpectedDestinationContainerRevision { get; }

        public int DestinationSlotIndex { get; }

        public Guid TargetItemInstanceId { get; }

        public long ExpectedTargetItemRevision { get; }

        public Guid CorpseBagContentsContainerId { get; }

        public long ExpectedCorpseBagContentsRevision { get; }

        public Guid PlayerBagItemInstanceId { get; }

        public long ExpectedPlayerBagRevision { get; }

        public Guid PlayerBagContentsContainerId { get; }

        public long ExpectedPlayerBagContentsRevision { get; }

        public static RealtimeCorpseInteractionIntent CreateOpen(
            Guid operationId,
            Guid corpseId)
        {
            return CreateSimple(operationId, RealtimeCorpseInteractionKind.Open, corpseId, -1);
        }

        public static RealtimeCorpseInteractionIntent CreateClose(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision)
        {
            return CreateSimple(
                operationId,
                RealtimeCorpseInteractionKind.Close,
                corpseId,
                expectedCorpseRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateRefresh(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision)
        {
            return CreateSimple(
                operationId,
                RealtimeCorpseInteractionKind.Refresh,
                corpseId,
                expectedCorpseRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateLootItem(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.LootItem,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                0,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateLootPartialStack(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.LootPartialStack,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                quantity,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateDepositItem(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.DepositItem,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                0,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateDepositPartialStack(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.DepositPartialStack,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                quantity,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateMoveItem(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.MoveItem,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                0,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateMovePartialStack(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return CreateItemTransfer(
                operationId,
                RealtimeCorpseInteractionKind.MovePartialStack,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                quantity,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision);
        }

        public static RealtimeCorpseInteractionIntent CreateSwapBag(
            Guid operationId,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid corpseBagItemInstanceId,
            long expectedCorpseBagRevision,
            Guid corpseBagContentsContainerId,
            long expectedCorpseBagContentsRevision,
            Guid playerBagItemInstanceId,
            long expectedPlayerBagRevision,
            Guid playerBagContentsContainerId,
            long expectedPlayerBagContentsRevision)
        {
            return new RealtimeCorpseInteractionIntent(
                operationId,
                RealtimeCorpseInteractionKind.SwapBag,
                corpseId,
                expectedCorpseRevision,
                corpseBagItemInstanceId,
                expectedCorpseBagRevision,
                0,
                Guid.Empty,
                0,
                -1,
                Guid.Empty,
                0,
                corpseBagContentsContainerId,
                expectedCorpseBagContentsRevision,
                playerBagItemInstanceId,
                expectedPlayerBagRevision,
                playerBagContentsContainerId,
                expectedPlayerBagContentsRevision);
        }

        private static RealtimeCorpseInteractionIntent CreateSimple(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            long expectedCorpseRevision)
        {
            return new RealtimeCorpseInteractionIntent(
                operationId,
                operationKind,
                corpseId,
                expectedCorpseRevision,
                Guid.Empty,
                0,
                0,
                Guid.Empty,
                0,
                -1,
                Guid.Empty,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                0);
        }

        private static RealtimeCorpseInteractionIntent CreateItemTransfer(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            long expectedCorpseRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            long expectedDestinationContainerRevision,
            int destinationSlotIndex,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return new RealtimeCorpseInteractionIntent(
                operationId,
                operationKind,
                corpseId,
                expectedCorpseRevision,
                itemInstanceId,
                expectedItemRevision,
                quantity,
                destinationContainerId,
                expectedDestinationContainerRevision,
                destinationSlotIndex,
                targetItemInstanceId,
                expectedTargetItemRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                0);
        }
    }

    public sealed class RealtimeCorpseInteractionResult
    {
        public RealtimeCorpseInteractionResult(
            Guid operationId,
            RealtimeCorpseInteractionKind operationKind,
            Guid corpseId,
            bool succeeded,
            bool requiresCorpseRefresh,
            bool requiresInventoryRefresh,
            RealtimeError error,
            RealtimeCarryState carryState)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            CorpseId = corpseId;
            Succeeded = succeeded;
            RequiresCorpseRefresh = requiresCorpseRefresh;
            RequiresInventoryRefresh = requiresInventoryRefresh;
            Error = error;
            CarryState = carryState;
        }

        public Guid OperationId { get; }

        public RealtimeCorpseInteractionKind OperationKind { get; }

        public Guid CorpseId { get; }

        public bool Succeeded { get; }

        public bool RequiresCorpseRefresh { get; }

        public bool RequiresInventoryRefresh { get; }

        public RealtimeError Error { get; }

        public RealtimeCarryState CarryState { get; }
    }

    public sealed class RealtimeCorpseItem
    {
        public RealtimeCorpseItem(
            Guid itemInstanceId,
            string definitionId,
            int quantity,
            long revision,
            Guid bagContentsContainerId,
            long bagContentsRevision)
        {
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Revision = revision;
            BagContentsContainerId = bagContentsContainerId;
            BagContentsRevision = bagContentsRevision;
        }

        public Guid ItemInstanceId { get; }

        public string DefinitionId { get; }

        public int Quantity { get; }

        public long Revision { get; }

        public Guid BagContentsContainerId { get; }

        public long BagContentsRevision { get; }
    }

    public sealed class RealtimeCorpseSlot
    {
        public RealtimeCorpseSlot(int slotIndex, string slotKind, RealtimeCorpseItem item)
            : this(slotIndex, slotKind, Array.Empty<string>(), string.Empty, item)
        {
        }

        public RealtimeCorpseSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            RealtimeCorpseItem item)
            : this(slotIndex, slotKind, acceptedTags, string.Empty, item)
        {
        }

        public RealtimeCorpseSlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            string equipmentSlotId,
            RealtimeCorpseItem item)
        {
            SlotIndex = slotIndex;
            SlotKind = slotKind;
            AcceptedTags = acceptedTags ?? Array.Empty<string>();
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            Item = item;
        }

        public int SlotIndex { get; }

        public string SlotKind { get; }

        public string[] AcceptedTags { get; }

        public string EquipmentSlotId { get; }

        public RealtimeCorpseItem Item { get; }
    }

    public sealed class RealtimeCorpseViewStateChunk
    {
        public RealtimeCorpseViewStateChunk(
            Guid updateId,
            RealtimeCorpseViewUpdateKind updateKind,
            Guid corpseId,
            long baseRevision,
            long revision,
            long expiresAtUnixMilliseconds,
            string sourceDisplayName,
            string presentationKey,
            ushort chunkIndex,
            ushort chunkCount,
            string sectionKind,
            Guid containerId,
            long containerRevision,
            bool replaceSection,
            int sectionSlotCapacity,
            int slotOffset,
            RealtimeCorpseSlot[] slots)
        {
            UpdateId = updateId;
            UpdateKind = updateKind;
            CorpseId = corpseId;
            BaseRevision = baseRevision;
            Revision = revision;
            ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
            SourceDisplayName = sourceDisplayName;
            PresentationKey = presentationKey;
            ChunkIndex = chunkIndex;
            ChunkCount = chunkCount;
            SectionKind = sectionKind;
            ContainerId = containerId;
            ContainerRevision = containerRevision;
            ReplaceSection = replaceSection;
            SectionSlotCapacity = sectionSlotCapacity;
            SlotOffset = slotOffset;
            Slots = slots ?? Array.Empty<RealtimeCorpseSlot>();
        }

        public Guid UpdateId { get; }

        public RealtimeCorpseViewUpdateKind UpdateKind { get; }

        public Guid CorpseId { get; }

        public long BaseRevision { get; }

        public long Revision { get; }

        public long ExpiresAtUnixMilliseconds { get; }

        public string SourceDisplayName { get; }

        public string PresentationKey { get; }

        public ushort ChunkIndex { get; }

        public ushort ChunkCount { get; }

        public string SectionKind { get; }

        public Guid ContainerId { get; }

        public long ContainerRevision { get; }

        public bool ReplaceSection { get; }

        public int SectionSlotCapacity { get; }

        public int SlotOffset { get; }

        public RealtimeCorpseSlot[] Slots { get; }
    }

    public sealed class RealtimeCorpseViewClosed
    {
        public RealtimeCorpseViewClosed(Guid corpseId, string code, string message)
        {
            CorpseId = corpseId;
            Code = code;
            Message = message;
        }

        public Guid CorpseId { get; }

        public string Code { get; }

        public string Message { get; }
    }
}
