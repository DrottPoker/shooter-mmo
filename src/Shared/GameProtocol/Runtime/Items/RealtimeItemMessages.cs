using System;

namespace Mmorpg.GameProtocol
{
    public enum RealtimeItemOperationKind : byte
    {
        Relocate = 1,
        Equip = 2,
        Unequip = 3,
        SplitStack = 4,
        MergeStacks = 5,
        Destroy = 6,
        ClaimRecoveryDelivery = 7,
        SwapContainerItems = 8
    }

    public sealed class RealtimeCarryState
    {
        public RealtimeCarryState(
            long itemStateRevision,
            long carriedWeight,
            long carryCapacity)
        {
            ItemStateRevision = itemStateRevision;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
        }

        public long ItemStateRevision { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }
    }

    public sealed class RealtimeItemRevisionExpectation
    {
        public RealtimeItemRevisionExpectation(Guid itemInstanceId, long revision)
        {
            ItemInstanceId = itemInstanceId;
            Revision = revision;
        }

        public Guid ItemInstanceId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeItemOperationIntent
    {
        internal RealtimeItemOperationIntent(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex,
            int quantity,
            string equipmentSlotId,
            Guid recoveryDeliveryId,
            long expectedRecoveryDeliveryRevision,
            RealtimeItemRevisionExpectation[] items)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            ExpectedCharacterRevision = expectedCharacterRevision;
            ItemInstanceId = itemInstanceId;
            ExpectedItemRevision = expectedItemRevision;
            TargetItemInstanceId = targetItemInstanceId;
            ExpectedTargetItemRevision = expectedTargetItemRevision;
            DestinationContainerId = destinationContainerId;
            DestinationSlotIndex = destinationSlotIndex;
            Quantity = quantity;
            EquipmentSlotId = equipmentSlotId;
            RecoveryDeliveryId = recoveryDeliveryId;
            ExpectedRecoveryDeliveryRevision = expectedRecoveryDeliveryRevision;
            Items = items;
        }

        public Guid OperationId { get; }

        public RealtimeItemOperationKind OperationKind { get; }

        public long ExpectedCharacterRevision { get; }

        public Guid ItemInstanceId { get; }

        public long ExpectedItemRevision { get; }

        public Guid TargetItemInstanceId { get; }

        public long ExpectedTargetItemRevision { get; }

        public Guid DestinationContainerId { get; }

        public int DestinationSlotIndex { get; }

        public int Quantity { get; }

        public string EquipmentSlotId { get; }

        public Guid RecoveryDeliveryId { get; }

        public long ExpectedRecoveryDeliveryRevision { get; }

        public RealtimeItemRevisionExpectation[] Items { get; }

        public static RealtimeItemOperationIntent CreateRelocate(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return CreateItemDestinationOperation(
                operationId,
                RealtimeItemOperationKind.Relocate,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex);
        }

        public static RealtimeItemOperationIntent CreateEquip(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            string equipmentSlotId)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.Equip,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                0,
                equipmentSlotId,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateUnequip(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return CreateItemDestinationOperation(
                operationId,
                RealtimeItemOperationKind.Unequip,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                destinationContainerId,
                destinationSlotIndex);
        }

        public static RealtimeItemOperationIntent CreateSplitStack(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex = -1)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.SplitStack,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                destinationContainerId,
                destinationSlotIndex,
                quantity,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateMergeStacks(
            Guid operationId,
            long expectedCharacterRevision,
            Guid sourceItemInstanceId,
            long expectedSourceItemRevision,
            Guid targetItemInstanceId,
            long expectedTargetItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.MergeStacks,
                expectedCharacterRevision,
                sourceItemInstanceId,
                expectedSourceItemRevision,
                targetItemInstanceId,
                expectedTargetItemRevision,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateSwapContainerItems(
            Guid operationId,
            long expectedCharacterRevision,
            Guid firstItemInstanceId,
            long expectedFirstItemRevision,
            Guid secondItemInstanceId,
            long expectedSecondItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.SwapContainerItems,
                expectedCharacterRevision,
                firstItemInstanceId,
                expectedFirstItemRevision,
                secondItemInstanceId,
                expectedSecondItemRevision,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateDestroy(
            Guid operationId,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.Destroy,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                -1,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }

        public static RealtimeItemOperationIntent CreateClaimRecoveryDelivery(
            Guid operationId,
            long expectedCharacterRevision,
            Guid recoveryDeliveryId,
            long expectedRecoveryDeliveryRevision,
            Guid destinationContainerId,
            RealtimeItemRevisionExpectation[] items)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                RealtimeItemOperationKind.ClaimRecoveryDelivery,
                expectedCharacterRevision,
                Guid.Empty,
                0,
                Guid.Empty,
                0,
                destinationContainerId,
                -1,
                0,
                string.Empty,
                recoveryDeliveryId,
                expectedRecoveryDeliveryRevision,
                items);
        }

        private static RealtimeItemOperationIntent CreateItemDestinationOperation(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            long expectedCharacterRevision,
            Guid itemInstanceId,
            long expectedItemRevision,
            Guid destinationContainerId,
            int destinationSlotIndex)
        {
            return new RealtimeItemOperationIntent(
                operationId,
                operationKind,
                expectedCharacterRevision,
                itemInstanceId,
                expectedItemRevision,
                Guid.Empty,
                0,
                destinationContainerId,
                destinationSlotIndex,
                0,
                string.Empty,
                Guid.Empty,
                0,
                Array.Empty<RealtimeItemRevisionExpectation>());
        }
    }

    public sealed class RealtimeItemRevision
    {
        public RealtimeItemRevision(Guid itemInstanceId, long revision)
        {
            ItemInstanceId = itemInstanceId;
            Revision = revision;
        }

        public Guid ItemInstanceId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeContainerRevision
    {
        public RealtimeContainerRevision(Guid containerId, long revision)
        {
            ContainerId = containerId;
            Revision = revision;
        }

        public Guid ContainerId { get; }

        public long Revision { get; }
    }

    public sealed class RealtimeItemOperationResult
    {
        public RealtimeItemOperationResult(
            Guid operationId,
            RealtimeItemOperationKind operationKind,
            bool succeeded,
            bool requiresInventoryRefresh,
            RealtimeError error,
            RealtimeCarryState carryState,
            RealtimeItemRevision[] itemRevisions,
            RealtimeContainerRevision[] containerRevisions,
            Guid[] recoveryDeliveryIds)
        {
            OperationId = operationId;
            OperationKind = operationKind;
            Succeeded = succeeded;
            RequiresInventoryRefresh = requiresInventoryRefresh;
            Error = error;
            CarryState = carryState;
            ItemRevisions = itemRevisions;
            ContainerRevisions = containerRevisions;
            RecoveryDeliveryIds = recoveryDeliveryIds;
        }

        public Guid OperationId { get; }

        public RealtimeItemOperationKind OperationKind { get; }

        public bool Succeeded { get; }

        public bool RequiresInventoryRefresh { get; }

        public RealtimeError Error { get; }

        public RealtimeCarryState CarryState { get; }

        public RealtimeItemRevision[] ItemRevisions { get; }

        public RealtimeContainerRevision[] ContainerRevisions { get; }

        public Guid[] RecoveryDeliveryIds { get; }
    }
}
