using System;
using System.IO;

namespace Mmorpg.GameProtocol
{
    // In-world item operations and carry state.
    public static partial class RealtimeProtocol
    {
        public static byte[] EncodeCarryStateChanged(RealtimeCarryState carryState)
        {
            return Encode(RealtimeMessageType.CarryStateChanged, writer =>
            {
                WriteCarryState(writer, carryState);
            });
        }

        public static bool TryDecodeCarryStateChanged(
            byte[] data,
            out RealtimeCarryState carryState,
            out string error)
        {
            carryState = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CarryStateChanged,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                return TryReadCarryState(reader, out carryState, out error)
                    && TryFinish(stream, out error);
            }
        }

        public static byte[] EncodeItemOperationIntent(RealtimeItemOperationIntent intent)
        {
            if (!IsValidItemOperationIntent(intent))
            {
                throw new ArgumentException("Item operation intent is invalid.", nameof(intent));
            }

            return Encode(RealtimeMessageType.ItemOperationIntent, writer =>
            {
                WriteGuid(writer, intent.OperationId);
                writer.Write((byte)intent.OperationKind);
                writer.Write(intent.ExpectedCharacterRevision);
                switch (intent.OperationKind)
                {
                    case RealtimeItemOperationKind.Relocate:
                    case RealtimeItemOperationKind.Unequip:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write(intent.DestinationSlotIndex);
                        break;
                    case RealtimeItemOperationKind.Equip:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteString(
                            writer,
                            intent.EquipmentSlotId,
                            MaximumEquipmentSlotIdLength,
                            nameof(intent.EquipmentSlotId));
                        break;
                    case RealtimeItemOperationKind.SplitStack:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        writer.Write(intent.Quantity);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write(intent.DestinationSlotIndex);
                        break;
                    case RealtimeItemOperationKind.MergeStacks:
                    case RealtimeItemOperationKind.SwapContainerItems:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteItemExpectation(
                            writer,
                            intent.TargetItemInstanceId,
                            intent.ExpectedTargetItemRevision);
                        break;
                    case RealtimeItemOperationKind.Destroy:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        break;
                    case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                        WriteGuid(writer, intent.RecoveryDeliveryId);
                        writer.Write(intent.ExpectedRecoveryDeliveryRevision);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write((byte)intent.Items.Length);
                        for (var index = 0; index < intent.Items.Length; index++)
                        {
                            WriteItemExpectation(
                                writer,
                                intent.Items[index].ItemInstanceId,
                                intent.Items[index].Revision);
                        }

                        break;
                    default:
                        throw new ArgumentException(
                            "Item operation kind is invalid.",
                            nameof(intent));
                }
            });
        }

        public static bool TryDecodeItemOperationIntent(
            byte[] data,
            out RealtimeItemOperationIntent intent,
            out string error)
        {
            intent = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.ItemOperationIntent,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadInt64(reader, out var expectedCharacterRevision, out error))
                {
                    return false;
                }

                var operationKind = (RealtimeItemOperationKind)rawOperationKind;
                RealtimeItemOperationIntent decoded;
                switch (operationKind)
                {
                    case RealtimeItemOperationKind.Relocate:
                    case RealtimeItemOperationKind.Unequip:
                        if (!TryReadItemExpectation(
                                reader,
                                out var itemId,
                                out var itemRevision,
                                out error)
                            || !TryReadGuid(reader, out var destinationId, out error)
                            || !TryReadInt32(reader, out var destinationSlot, out error))
                        {
                            return false;
                        }

                        decoded = new RealtimeItemOperationIntent(
                            operationId,
                            operationKind,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            Guid.Empty,
                            0,
                            destinationId,
                            destinationSlot,
                            0,
                            string.Empty,
                            Guid.Empty,
                            0,
                            Array.Empty<RealtimeItemRevisionExpectation>());
                        break;
                    case RealtimeItemOperationKind.Equip:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadString(
                                reader,
                                MaximumEquipmentSlotIdLength,
                                out var equipmentSlotId,
                                out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateEquip(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            equipmentSlotId);
                        break;
                    case RealtimeItemOperationKind.SplitStack:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadInt32(reader, out var quantity, out error)
                            || !TryReadGuid(reader, out destinationId, out error)
                            || !TryReadInt32(reader, out destinationSlot, out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateSplitStack(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision,
                            quantity,
                            destinationId,
                            destinationSlot);
                        break;
                    case RealtimeItemOperationKind.MergeStacks:
                    case RealtimeItemOperationKind.SwapContainerItems:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error)
                            || !TryReadItemExpectation(
                                reader,
                                out var targetItemId,
                                out var targetItemRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = operationKind == RealtimeItemOperationKind.MergeStacks
                            ? RealtimeItemOperationIntent.CreateMergeStacks(
                                operationId,
                                expectedCharacterRevision,
                                itemId,
                                itemRevision,
                                targetItemId,
                                targetItemRevision)
                            : RealtimeItemOperationIntent.CreateSwapContainerItems(
                                operationId,
                                expectedCharacterRevision,
                                itemId,
                                itemRevision,
                                targetItemId,
                                targetItemRevision);
                        break;
                    case RealtimeItemOperationKind.Destroy:
                        if (!TryReadItemExpectation(
                                reader,
                                out itemId,
                                out itemRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = RealtimeItemOperationIntent.CreateDestroy(
                            operationId,
                            expectedCharacterRevision,
                            itemId,
                            itemRevision);
                        break;
                    case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                        if (!TryReadGuid(reader, out var recoveryDeliveryId, out error)
                            || !TryReadInt64(
                                reader,
                                out var expectedRecoveryRevision,
                                out error)
                            || !TryReadGuid(reader, out destinationId, out error)
                            || !TryReadByte(reader, out var itemCount, out error)
                            || itemCount == 0
                            || itemCount > MaximumItemOperationItems)
                        {
                            if (string.IsNullOrEmpty(error))
                            {
                                error = "Recovery claim item count is invalid.";
                            }

                            return false;
                        }

                        var items = new RealtimeItemRevisionExpectation[itemCount];
                        for (var index = 0; index < itemCount; index++)
                        {
                            if (!TryReadItemExpectation(
                                    reader,
                                    out var recoveryItemId,
                                    out var recoveryItemRevision,
                                    out error))
                            {
                                return false;
                            }

                            items[index] = new RealtimeItemRevisionExpectation(
                                recoveryItemId,
                                recoveryItemRevision);
                        }

                        decoded = RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                            operationId,
                            expectedCharacterRevision,
                            recoveryDeliveryId,
                            expectedRecoveryRevision,
                            destinationId,
                            items);
                        break;
                    default:
                        error = "Item operation kind is invalid.";
                        return false;
                }

                if (!IsValidItemOperationIntent(decoded) || !TryFinish(stream, out error))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Item operation intent values are invalid.";
                    }

                    return false;
                }

                intent = decoded;
                return true;
            }
        }

        public static byte[] EncodeItemOperationResult(RealtimeItemOperationResult result)
        {
            if (!IsValidItemOperationResult(result))
            {
                throw new ArgumentException("Item operation result is invalid.", nameof(result));
            }

            return Encode(RealtimeMessageType.ItemOperationResult, writer =>
            {
                WriteGuid(writer, result.OperationId);
                writer.Write((byte)result.OperationKind);
                writer.Write(result.Succeeded);
                writer.Write(result.RequiresInventoryRefresh);
                WriteCarryState(writer, result.CarryState);
                if (!result.Succeeded)
                {
                    WriteString(
                        writer,
                        result.Error.Code,
                        MaximumErrorCodeLength,
                        nameof(result.Error.Code));
                    WriteString(
                        writer,
                        result.Error.Message,
                        MaximumErrorMessageLength,
                        nameof(result.Error.Message));
                }

                writer.Write((byte)result.ItemRevisions.Length);
                for (var index = 0; index < result.ItemRevisions.Length; index++)
                {
                    WriteItemExpectation(
                        writer,
                        result.ItemRevisions[index].ItemInstanceId,
                        result.ItemRevisions[index].Revision);
                }

                writer.Write((byte)result.ContainerRevisions.Length);
                for (var index = 0; index < result.ContainerRevisions.Length; index++)
                {
                    WriteGuid(writer, result.ContainerRevisions[index].ContainerId);
                    writer.Write(result.ContainerRevisions[index].Revision);
                }

                writer.Write((byte)result.RecoveryDeliveryIds.Length);
                for (var index = 0; index < result.RecoveryDeliveryIds.Length; index++)
                {
                    WriteGuid(writer, result.RecoveryDeliveryIds[index]);
                }
            });
        }

        public static bool TryDecodeItemOperationResult(
            byte[] data,
            out RealtimeItemOperationResult result,
            out string error)
        {
            result = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.ItemOperationResult,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var operationId, out error)
                    || !TryReadByte(reader, out var rawOperationKind, out error)
                    || !TryReadBoolean(reader, out var succeeded, out error)
                    || !TryReadBoolean(reader, out var requiresRefresh, out error)
                    || !TryReadCarryState(reader, out var carryState, out error))
                {
                    return false;
                }

                RealtimeError operationError = null;
                if (!succeeded)
                {
                    if (!TryReadString(
                            reader,
                            MaximumErrorCodeLength,
                            out var errorCode,
                            out error)
                        || !TryReadString(
                            reader,
                            MaximumErrorMessageLength,
                            out var errorMessage,
                            out error))
                    {
                        return false;
                    }

                    operationError = new RealtimeError(errorCode, errorMessage);
                }

                if (!TryReadByte(reader, out var itemRevisionCount, out error)
                    || itemRevisionCount > MaximumItemResultItemRevisions)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result revision count is invalid."
                        : error;
                    return false;
                }

                var itemRevisions = new RealtimeItemRevision[itemRevisionCount];
                for (var index = 0; index < itemRevisionCount; index++)
                {
                    if (!TryReadItemExpectation(
                            reader,
                            out var itemId,
                            out var revision,
                            out error))
                    {
                        return false;
                    }

                    itemRevisions[index] = new RealtimeItemRevision(itemId, revision);
                }

                if (!TryReadByte(reader, out var containerRevisionCount, out error)
                    || containerRevisionCount > MaximumItemResultContainerRevisions)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result container count is invalid."
                        : error;
                    return false;
                }

                var containerRevisions = new RealtimeContainerRevision[containerRevisionCount];
                for (var index = 0; index < containerRevisionCount; index++)
                {
                    if (!TryReadGuid(reader, out var containerId, out error)
                        || !TryReadInt64(reader, out var revision, out error))
                    {
                        return false;
                    }

                    containerRevisions[index] = new RealtimeContainerRevision(
                        containerId,
                        revision);
                }

                if (!TryReadByte(reader, out var deliveryCount, out error)
                    || deliveryCount > MaximumItemResultRecoveryDeliveries)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Item result Recovery delivery count is invalid."
                        : error;
                    return false;
                }

                var recoveryDeliveryIds = new Guid[deliveryCount];
                for (var index = 0; index < deliveryCount; index++)
                {
                    if (!TryReadGuid(reader, out recoveryDeliveryIds[index], out error))
                    {
                        return false;
                    }
                }

                var decoded = new RealtimeItemOperationResult(
                    operationId,
                    (RealtimeItemOperationKind)rawOperationKind,
                    succeeded,
                    requiresRefresh,
                    operationError,
                    carryState,
                    itemRevisions,
                    containerRevisions,
                    recoveryDeliveryIds);
                if (!IsValidItemOperationResult(decoded) || !TryFinish(stream, out error))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Item operation result values are invalid.";
                    }

                    return false;
                }

                result = decoded;
                return true;
            }
        }

        private static void WriteItemExpectation(
            BinaryWriter writer,
            Guid itemInstanceId,
            long revision)
        {
            WriteGuid(writer, itemInstanceId);
            writer.Write(revision);
        }

        private static bool TryReadItemExpectation(
            BinaryReader reader,
            out Guid itemInstanceId,
            out long revision,
            out string error)
        {
            revision = 0;
            return TryReadGuid(reader, out itemInstanceId, out error)
                && TryReadInt64(reader, out revision, out error);
        }

        private static void WriteCarryState(BinaryWriter writer, RealtimeCarryState carryState)
        {
            if (!IsValidCarryState(carryState))
            {
                throw new ArgumentException("Carry state is invalid.", nameof(carryState));
            }

            writer.Write(carryState.ItemStateRevision);
            writer.Write(carryState.CarriedWeight);
            writer.Write(carryState.CarryCapacity);
        }

        private static bool TryReadCarryState(
            BinaryReader reader,
            out RealtimeCarryState carryState,
            out string error)
        {
            carryState = null;
            if (!TryReadInt64(reader, out var itemStateRevision, out error)
                || !TryReadInt64(reader, out var carriedWeight, out error)
                || !TryReadInt64(reader, out var carryCapacity, out error))
            {
                return false;
            }

            var decoded = new RealtimeCarryState(
                itemStateRevision,
                carriedWeight,
                carryCapacity);
            if (!IsValidCarryState(decoded))
            {
                error = "Carry state is invalid.";
                return false;
            }

            carryState = decoded;
            return true;
        }

        private static bool IsValidItemOperationIntent(RealtimeItemOperationIntent intent)
        {
            if (intent == null
                || intent.OperationId == Guid.Empty
                || intent.ExpectedCharacterRevision < 0
                || !Enum.IsDefined(typeof(RealtimeItemOperationKind), intent.OperationKind))
            {
                return false;
            }

            switch (intent.OperationKind)
            {
                case RealtimeItemOperationKind.Relocate:
                case RealtimeItemOperationKind.Unequip:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && intent.DestinationContainerId != Guid.Empty
                        && IsValidDestinationSlot(intent.DestinationSlotIndex);
                case RealtimeItemOperationKind.Equip:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && !string.IsNullOrWhiteSpace(intent.EquipmentSlotId)
                        && intent.EquipmentSlotId.Length <= MaximumEquipmentSlotIdLength;
                case RealtimeItemOperationKind.SplitStack:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && intent.Quantity > 0
                        && intent.DestinationContainerId != Guid.Empty
                        && IsValidDestinationSlot(intent.DestinationSlotIndex);
                case RealtimeItemOperationKind.MergeStacks:
                case RealtimeItemOperationKind.SwapContainerItems:
                    return IsValidItemExpectation(
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision)
                        && IsValidItemExpectation(
                            intent.TargetItemInstanceId,
                            intent.ExpectedTargetItemRevision)
                        && intent.ItemInstanceId != intent.TargetItemInstanceId;
                case RealtimeItemOperationKind.Destroy:
                    return IsValidItemExpectation(
                        intent.ItemInstanceId,
                        intent.ExpectedItemRevision);
                case RealtimeItemOperationKind.ClaimRecoveryDelivery:
                    return intent.RecoveryDeliveryId != Guid.Empty
                        && intent.ExpectedRecoveryDeliveryRevision >= 0
                        && intent.DestinationContainerId != Guid.Empty
                        && intent.Items != null
                        && intent.Items.Length > 0
                        && intent.Items.Length <= MaximumItemOperationItems
                        && Array.TrueForAll(
                            intent.Items,
                            item => item != null
                                && IsValidItemExpectation(
                                    item.ItemInstanceId,
                                    item.Revision));
                default:
                    return false;
            }
        }

        private static bool IsValidItemOperationResult(RealtimeItemOperationResult result)
        {
            if (result == null
                || result.OperationId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeItemOperationKind), result.OperationKind)
                || !IsValidCarryState(result.CarryState)
                || result.ItemRevisions == null
                || result.ItemRevisions.Length > MaximumItemResultItemRevisions
                || result.ContainerRevisions == null
                || result.ContainerRevisions.Length > MaximumItemResultContainerRevisions
                || result.RecoveryDeliveryIds == null
                || result.RecoveryDeliveryIds.Length > MaximumItemResultRecoveryDeliveries)
            {
                return false;
            }

            if (result.Succeeded != (result.Error == null))
            {
                return false;
            }

            if (!result.Succeeded
                && (string.IsNullOrWhiteSpace(result.Error.Code)
                    || result.Error.Code.Length > MaximumErrorCodeLength
                    || string.IsNullOrWhiteSpace(result.Error.Message)
                    || result.Error.Message.Length > MaximumErrorMessageLength
                    || result.ItemRevisions.Length != 0
                    || result.ContainerRevisions.Length != 0
                    || result.RecoveryDeliveryIds.Length != 0))
            {
                return false;
            }

            return Array.TrueForAll(
                    result.ItemRevisions,
                    revision => revision != null
                        && IsValidItemExpectation(
                            revision.ItemInstanceId,
                            revision.Revision))
                && Array.TrueForAll(
                    result.ContainerRevisions,
                    revision => revision != null
                        && revision.ContainerId != Guid.Empty
                        && revision.Revision >= 0)
                && Array.TrueForAll(
                    result.RecoveryDeliveryIds,
                    deliveryId => deliveryId != Guid.Empty);
        }

        private static bool IsValidItemExpectation(Guid itemInstanceId, long revision)
        {
            return itemInstanceId != Guid.Empty && revision >= 0;
        }

        private static bool IsValidDestinationSlot(int slotIndex)
        {
            return slotIndex >= -1;
        }

        // Weight above the hard cap is valid: death can leave an involuntary overflow.
        private static bool IsValidCarryState(RealtimeCarryState carryState)
        {
            return carryState != null
                && carryState.ItemStateRevision >= 0
                && carryState.CarriedWeight >= 0
                && carryState.CarryCapacity > 0;
        }
    }
}
