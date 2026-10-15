using System;
using System.Collections.Generic;
using System.Linq;

namespace Mmorpg.GameProtocol
{
    // Corpse presence, interaction, and corpse view state.
    public static partial class RealtimeProtocol
    {
        public static byte[] EncodeCorpsePresenceSnapshotChunk(
            RealtimeCorpsePresenceSnapshotChunk chunk)
        {
            if (!IsValidCorpsePresenceChunk(chunk))
            {
                throw new ArgumentException("Corpse presence chunk is invalid.", nameof(chunk));
            }

            return Encode(RealtimeMessageType.CorpsePresenceSnapshotChunk, writer =>
            {
                writer.Write(chunk.SnapshotSequence);
                writer.Write(chunk.ChunkIndex);
                writer.Write(chunk.ChunkCount);
                writer.Write((byte)chunk.Corpses.Length);
                foreach (var corpse in chunk.Corpses)
                {
                    WriteGuid(writer, corpse.CorpseId);
                    writer.Write(corpse.PositionX);
                    writer.Write(corpse.PositionY);
                    writer.Write(corpse.PositionZ);
                    writer.Write(corpse.ExpiresAtUnixMilliseconds);
                    writer.Write(corpse.IsEmpty);
                    WriteString(
                        writer,
                        corpse.SourceDisplayName,
                        MaximumCorpseSourceNameLength,
                        nameof(corpse.SourceDisplayName));
                    WriteString(
                        writer,
                        corpse.PresentationKey,
                        MaximumCorpsePresentationKeyLength,
                        nameof(corpse.PresentationKey));
                }
            });
        }

        public static bool TryDecodeCorpsePresenceSnapshotChunk(
            byte[] data,
            out RealtimeCorpsePresenceSnapshotChunk chunk,
            out string error)
        {
            chunk = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CorpsePresenceSnapshotChunk,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt32(reader, out var sequence, out error)
                    || !TryReadUInt16(reader, out var chunkIndex, out error)
                    || !TryReadUInt16(reader, out var chunkCount, out error)
                    || !TryReadByte(reader, out var corpseCount, out error)
                    || corpseCount > MaximumCorpsePresencePerChunk)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse presence count is invalid."
                        : error;
                    return false;
                }

                var corpses = new RealtimeCorpsePresence[corpseCount];
                for (var index = 0; index < corpses.Length; index++)
                {
                    if (!TryReadGuid(reader, out var corpseId, out error)
                        || !TryReadSingle(reader, out var x, out error)
                        || !TryReadSingle(reader, out var y, out error)
                        || !TryReadSingle(reader, out var z, out error)
                        || !TryReadInt64(reader, out var expiresAt, out error)
                        || !TryReadBoolean(reader, out var isEmpty, out error)
                        || !TryReadString(
                            reader,
                            MaximumCorpseSourceNameLength,
                            out var sourceName,
                            out error)
                        || !TryReadString(
                            reader,
                            MaximumCorpsePresentationKeyLength,
                            out var presentationKey,
                            out error))
                    {
                        return false;
                    }

                    corpses[index] = new RealtimeCorpsePresence(
                        corpseId,
                        x,
                        y,
                        z,
                        expiresAt,
                        isEmpty,
                        sourceName,
                        presentationKey);
                }

                var decoded = new RealtimeCorpsePresenceSnapshotChunk(
                    sequence,
                    chunkIndex,
                    chunkCount,
                    corpses);
                if (!IsValidCorpsePresenceChunk(decoded) || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse presence chunk values are invalid."
                        : error;
                    return false;
                }

                chunk = decoded;
                return true;
            }
        }

        public static byte[] EncodeCorpseInteractionIntent(
            RealtimeCorpseInteractionIntent intent)
        {
            if (!IsValidCorpseInteractionIntent(intent))
            {
                throw new ArgumentException("Corpse interaction intent is invalid.", nameof(intent));
            }

            return Encode(RealtimeMessageType.CorpseInteractionIntent, writer =>
            {
                WriteGuid(writer, intent.OperationId);
                writer.Write((byte)intent.OperationKind);
                WriteGuid(writer, intent.CorpseId);
                writer.Write(intent.ExpectedCorpseRevision);
                switch (intent.OperationKind)
                {
                    case RealtimeCorpseInteractionKind.Open:
                    case RealtimeCorpseInteractionKind.Close:
                    case RealtimeCorpseInteractionKind.Refresh:
                        break;
                    case RealtimeCorpseInteractionKind.LootItem:
                    case RealtimeCorpseInteractionKind.LootPartialStack:
                    case RealtimeCorpseInteractionKind.DepositItem:
                    case RealtimeCorpseInteractionKind.DepositPartialStack:
                    case RealtimeCorpseInteractionKind.MoveItem:
                    case RealtimeCorpseInteractionKind.MovePartialStack:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        writer.Write(intent.Quantity);
                        WriteGuid(writer, intent.DestinationContainerId);
                        writer.Write(intent.ExpectedDestinationContainerRevision);
                        writer.Write(intent.DestinationSlotIndex);
                        var hasTarget = intent.TargetItemInstanceId != Guid.Empty;
                        writer.Write(hasTarget);
                        if (hasTarget)
                        {
                            WriteItemExpectation(
                                writer,
                                intent.TargetItemInstanceId,
                                intent.ExpectedTargetItemRevision);
                        }

                        break;
                    case RealtimeCorpseInteractionKind.SwapBag:
                        WriteItemExpectation(
                            writer,
                            intent.ItemInstanceId,
                            intent.ExpectedItemRevision);
                        WriteGuid(writer, intent.CorpseBagContentsContainerId);
                        writer.Write(intent.ExpectedCorpseBagContentsRevision);
                        WriteItemExpectation(
                            writer,
                            intent.PlayerBagItemInstanceId,
                            intent.ExpectedPlayerBagRevision);
                        WriteGuid(writer, intent.PlayerBagContentsContainerId);
                        writer.Write(intent.ExpectedPlayerBagContentsRevision);
                        break;
                    default:
                        throw new ArgumentException(
                            "Corpse interaction kind is invalid.",
                            nameof(intent));
                }
            });
        }

        public static bool TryDecodeCorpseInteractionIntent(
            byte[] data,
            out RealtimeCorpseInteractionIntent intent,
            out string error)
        {
            intent = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CorpseInteractionIntent,
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
                    || !TryReadByte(reader, out var rawKind, out error)
                    || !TryReadGuid(reader, out var corpseId, out error)
                    || !TryReadInt64(reader, out var corpseRevision, out error))
                {
                    return false;
                }

                var kind = (RealtimeCorpseInteractionKind)rawKind;
                RealtimeCorpseInteractionIntent decoded;
                switch (kind)
                {
                    case RealtimeCorpseInteractionKind.Open:
                        decoded = RealtimeCorpseInteractionIntent.CreateOpen(
                            operationId,
                            corpseId);
                        break;
                    case RealtimeCorpseInteractionKind.Close:
                        decoded = RealtimeCorpseInteractionIntent.CreateClose(
                            operationId,
                            corpseId,
                            corpseRevision);
                        break;
                    case RealtimeCorpseInteractionKind.Refresh:
                        decoded = RealtimeCorpseInteractionIntent.CreateRefresh(
                            operationId,
                            corpseId,
                            corpseRevision);
                        break;
                    case RealtimeCorpseInteractionKind.LootItem:
                    case RealtimeCorpseInteractionKind.LootPartialStack:
                    case RealtimeCorpseInteractionKind.DepositItem:
                    case RealtimeCorpseInteractionKind.DepositPartialStack:
                    case RealtimeCorpseInteractionKind.MoveItem:
                    case RealtimeCorpseInteractionKind.MovePartialStack:
                        if (!TryReadItemExpectation(
                                reader,
                                out var itemId,
                                out var itemRevision,
                                out error)
                            || !TryReadInt32(reader, out var quantity, out error)
                            || !TryReadGuid(reader, out var destinationId, out error)
                            || !TryReadInt64(
                                reader,
                                out var destinationRevision,
                                out error)
                            || !TryReadInt32(reader, out var destinationSlot, out error)
                            || !TryReadBoolean(reader, out var hasTarget, out error))
                        {
                            return false;
                        }

                        var targetId = Guid.Empty;
                        long targetRevision = 0;
                        if (hasTarget
                            && !TryReadItemExpectation(
                                reader,
                                out targetId,
                                out targetRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = kind switch
                        {
                            RealtimeCorpseInteractionKind.LootItem =>
                                RealtimeCorpseInteractionIntent.CreateLootItem(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision),
                            RealtimeCorpseInteractionKind.LootPartialStack =>
                                RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                quantity,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision),
                            RealtimeCorpseInteractionKind.DepositItem =>
                                RealtimeCorpseInteractionIntent.CreateDepositItem(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision),
                            RealtimeCorpseInteractionKind.DepositPartialStack =>
                                RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                quantity,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision),
                            RealtimeCorpseInteractionKind.MoveItem =>
                                RealtimeCorpseInteractionIntent.CreateMoveItem(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision),
                            _ => RealtimeCorpseInteractionIntent.CreateMovePartialStack(
                                operationId,
                                corpseId,
                                corpseRevision,
                                itemId,
                                itemRevision,
                                quantity,
                                destinationId,
                                destinationRevision,
                                destinationSlot,
                                targetId,
                                targetRevision)
                        };
                        break;
                    case RealtimeCorpseInteractionKind.SwapBag:
                        if (!TryReadItemExpectation(
                                reader,
                                out var corpseBagId,
                                out var corpseBagRevision,
                                out error)
                            || !TryReadGuid(
                                reader,
                                out var corpseBagContainerId,
                                out error)
                            || !TryReadInt64(
                                reader,
                                out var corpseBagContainerRevision,
                                out error)
                            || !TryReadItemExpectation(
                                reader,
                                out var playerBagId,
                                out var playerBagRevision,
                                out error)
                            || !TryReadGuid(
                                reader,
                                out var playerBagContainerId,
                                out error)
                            || !TryReadInt64(
                                reader,
                                out var playerBagContainerRevision,
                                out error))
                        {
                            return false;
                        }

                        decoded = RealtimeCorpseInteractionIntent.CreateSwapBag(
                            operationId,
                            corpseId,
                            corpseRevision,
                            corpseBagId,
                            corpseBagRevision,
                            corpseBagContainerId,
                            corpseBagContainerRevision,
                            playerBagId,
                            playerBagRevision,
                            playerBagContainerId,
                            playerBagContainerRevision);
                        break;
                    default:
                        error = "Corpse interaction kind is invalid.";
                        return false;
                }

                if (!IsValidCorpseInteractionIntent(decoded) || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse interaction intent values are invalid."
                        : error;
                    return false;
                }

                intent = decoded;
                return true;
            }
        }

        public static byte[] EncodeCorpseInteractionResult(
            RealtimeCorpseInteractionResult result)
        {
            if (!IsValidCorpseInteractionResult(result))
            {
                throw new ArgumentException("Corpse interaction result is invalid.", nameof(result));
            }

            return Encode(RealtimeMessageType.CorpseInteractionResult, writer =>
            {
                WriteGuid(writer, result.OperationId);
                writer.Write((byte)result.OperationKind);
                WriteGuid(writer, result.CorpseId);
                writer.Write(result.Succeeded);
                writer.Write(result.RequiresCorpseRefresh);
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
            });
        }

        public static bool TryDecodeCorpseInteractionResult(
            byte[] data,
            out RealtimeCorpseInteractionResult result,
            out string error)
        {
            result = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CorpseInteractionResult,
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
                    || !TryReadByte(reader, out var rawKind, out error)
                    || !TryReadGuid(reader, out var corpseId, out error)
                    || !TryReadBoolean(reader, out var succeeded, out error)
                    || !TryReadBoolean(reader, out var requiresCorpseRefresh, out error)
                    || !TryReadBoolean(reader, out var requiresInventoryRefresh, out error)
                    || !TryReadCarryState(reader, out var carryState, out error))
                {
                    return false;
                }

                RealtimeError operationError = null;
                var errorCode = string.Empty;
                var errorMessage = string.Empty;
                if (!succeeded
                    && (!TryReadString(
                            reader,
                            MaximumErrorCodeLength,
                            out errorCode,
                            out error)
                        || !TryReadString(
                            reader,
                            MaximumErrorMessageLength,
                            out errorMessage,
                            out error)))
                {
                    return false;
                }
                else if (!succeeded)
                {
                    operationError = new RealtimeError(errorCode, errorMessage);
                }

                var decoded = new RealtimeCorpseInteractionResult(
                    operationId,
                    (RealtimeCorpseInteractionKind)rawKind,
                    corpseId,
                    succeeded,
                    requiresCorpseRefresh,
                    requiresInventoryRefresh,
                    operationError,
                    carryState);
                if (!IsValidCorpseInteractionResult(decoded) || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse interaction result values are invalid."
                        : error;
                    return false;
                }

                result = decoded;
                return true;
            }
        }

        public static byte[] EncodeCorpseViewStateChunk(RealtimeCorpseViewStateChunk chunk)
        {
            if (!IsValidCorpseViewStateChunk(chunk))
            {
                throw new ArgumentException("Corpse view state chunk is invalid.", nameof(chunk));
            }

            return Encode(RealtimeMessageType.CorpseViewStateChunk, writer =>
            {
                WriteGuid(writer, chunk.UpdateId);
                writer.Write((byte)chunk.UpdateKind);
                WriteGuid(writer, chunk.CorpseId);
                writer.Write(chunk.BaseRevision);
                writer.Write(chunk.Revision);
                writer.Write(chunk.ExpiresAtUnixMilliseconds);
                WriteString(
                    writer,
                    chunk.SourceDisplayName,
                    MaximumCorpseSourceNameLength,
                    nameof(chunk.SourceDisplayName));
                WriteString(
                    writer,
                    chunk.PresentationKey,
                    MaximumCorpsePresentationKeyLength,
                    nameof(chunk.PresentationKey));
                writer.Write(chunk.ChunkIndex);
                writer.Write(chunk.ChunkCount);
                WriteString(
                    writer,
                    chunk.SectionKind,
                    MaximumCorpseSectionKindLength,
                    nameof(chunk.SectionKind));
                WriteGuid(writer, chunk.ContainerId);
                writer.Write(chunk.ContainerRevision);
                writer.Write(chunk.ReplaceSection);
                writer.Write(chunk.SectionSlotCapacity);
                writer.Write(chunk.SlotOffset);
                writer.Write((byte)chunk.Slots.Length);
                foreach (var slot in chunk.Slots)
                {
                    writer.Write(slot.SlotIndex);
                    WriteString(
                        writer,
                        slot.SlotKind,
                        MaximumCorpseSlotKindLength,
                        nameof(slot.SlotKind));
                    writer.Write((byte)slot.AcceptedTags.Length);
                    foreach (var tag in slot.AcceptedTags)
                    {
                        WriteString(
                            writer,
                            tag,
                            MaximumSlotTagLength,
                            nameof(slot.AcceptedTags));
                    }
                    var hasEquipmentSlotId = !string.IsNullOrWhiteSpace(
                        slot.EquipmentSlotId);
                    writer.Write(hasEquipmentSlotId);
                    if (hasEquipmentSlotId)
                    {
                        WriteString(
                            writer,
                            slot.EquipmentSlotId,
                            MaximumEquipmentSlotIdLength,
                            nameof(slot.EquipmentSlotId));
                    }
                    writer.Write(slot.Item != null);
                    if (slot.Item != null)
                    {
                        WriteGuid(writer, slot.Item.ItemInstanceId);
                        WriteString(
                            writer,
                            slot.Item.DefinitionId,
                            MaximumDefinitionIdLength,
                            nameof(slot.Item.DefinitionId));
                        writer.Write(slot.Item.Quantity);
                        writer.Write(slot.Item.Revision);
                        WriteGuid(writer, slot.Item.BagContentsContainerId);
                        writer.Write(slot.Item.BagContentsRevision);
                    }
                }
            });
        }

        public static bool TryDecodeCorpseViewStateChunk(
            byte[] data,
            out RealtimeCorpseViewStateChunk chunk,
            out string error)
        {
            chunk = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CorpseViewStateChunk,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var updateId, out error)
                    || !TryReadByte(reader, out var rawUpdateKind, out error)
                    || !TryReadGuid(reader, out var corpseId, out error)
                    || !TryReadInt64(reader, out var baseRevision, out error)
                    || !TryReadInt64(reader, out var revision, out error)
                    || !TryReadInt64(reader, out var expiresAt, out error)
                    || !TryReadString(
                        reader,
                        MaximumCorpseSourceNameLength,
                        out var sourceName,
                        out error)
                    || !TryReadString(
                        reader,
                        MaximumCorpsePresentationKeyLength,
                        out var presentationKey,
                        out error)
                    || !TryReadUInt16(reader, out var chunkIndex, out error)
                    || !TryReadUInt16(reader, out var chunkCount, out error)
                    || !TryReadString(
                        reader,
                        MaximumCorpseSectionKindLength,
                        out var sectionKind,
                        out error)
                    || !TryReadGuid(reader, out var containerId, out error)
                    || !TryReadInt64(reader, out var containerRevision, out error)
                    || !TryReadBoolean(reader, out var replaceSection, out error)
                    || !TryReadInt32(reader, out var sectionSlotCapacity, out error)
                    || !TryReadInt32(reader, out var slotOffset, out error)
                    || !TryReadByte(reader, out var slotCount, out error)
                    || slotCount > MaximumCorpseViewSlotsPerChunk)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse view slot count is invalid."
                        : error;
                    return false;
                }

                var slots = new RealtimeCorpseSlot[slotCount];
                for (var index = 0; index < slots.Length; index++)
                {
                    if (!TryReadInt32(reader, out var slotIndex, out error)
                        || !TryReadString(
                            reader,
                            MaximumCorpseSlotKindLength,
                            out var slotKind,
                            out error)
                        || !TryReadByte(reader, out var tagCount, out error)
                        || tagCount > MaximumSlotTags)
                    {
                        return false;
                    }

                    var acceptedTags = new string[tagCount];
                    for (var tagIndex = 0; tagIndex < acceptedTags.Length; tagIndex++)
                    {
                        if (!TryReadString(
                                reader,
                                MaximumSlotTagLength,
                                out acceptedTags[tagIndex],
                                out error))
                        {
                            return false;
                        }
                    }

                    if (!TryReadBoolean(reader, out var hasEquipmentSlotId, out error))
                    {
                        return false;
                    }

                    var equipmentSlotId = string.Empty;
                    if (hasEquipmentSlotId
                        && !TryReadString(
                            reader,
                            MaximumEquipmentSlotIdLength,
                            out equipmentSlotId,
                            out error))
                    {
                        return false;
                    }

                    if (!TryReadBoolean(reader, out var hasItem, out error))
                    {
                        return false;
                    }

                    RealtimeCorpseItem item = null;
                    if (hasItem)
                    {
                        if (!TryReadGuid(reader, out var itemId, out error)
                            || !TryReadString(
                                reader,
                                MaximumDefinitionIdLength,
                                out var definitionId,
                                out error)
                            || !TryReadInt32(reader, out var quantity, out error)
                            || !TryReadInt64(reader, out var itemRevision, out error)
                            || !TryReadGuid(reader, out var bagContainerId, out error)
                            || !TryReadInt64(reader, out var bagRevision, out error))
                        {
                            return false;
                        }

                        item = new RealtimeCorpseItem(
                            itemId,
                            definitionId,
                            quantity,
                            itemRevision,
                            bagContainerId,
                            bagRevision);
                    }

                    slots[index] = new RealtimeCorpseSlot(
                        slotIndex,
                        slotKind,
                        acceptedTags,
                        equipmentSlotId,
                        item);
                }

                var decoded = new RealtimeCorpseViewStateChunk(
                    updateId,
                    (RealtimeCorpseViewUpdateKind)rawUpdateKind,
                    corpseId,
                    baseRevision,
                    revision,
                    expiresAt,
                    sourceName,
                    presentationKey,
                    chunkIndex,
                    chunkCount,
                    sectionKind,
                    containerId,
                    containerRevision,
                    replaceSection,
                    sectionSlotCapacity,
                    slotOffset,
                    slots);
                if (!IsValidCorpseViewStateChunk(decoded) || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Corpse view state chunk values are invalid."
                        : error;
                    return false;
                }

                chunk = decoded;
                return true;
            }
        }

        public static byte[] EncodeCorpseViewClosed(RealtimeCorpseViewClosed closed)
        {
            if (closed == null
                || closed.CorpseId == Guid.Empty
                || string.IsNullOrWhiteSpace(closed.Code)
                || closed.Code.Length > MaximumErrorCodeLength
                || string.IsNullOrWhiteSpace(closed.Message)
                || closed.Message.Length > MaximumErrorMessageLength)
            {
                throw new ArgumentException("Corpse view closure is invalid.", nameof(closed));
            }

            return Encode(RealtimeMessageType.CorpseViewClosed, writer =>
            {
                WriteGuid(writer, closed.CorpseId);
                WriteString(writer, closed.Code, MaximumErrorCodeLength, nameof(closed.Code));
                WriteString(
                    writer,
                    closed.Message,
                    MaximumErrorMessageLength,
                    nameof(closed.Message));
            });
        }

        public static bool TryDecodeCorpseViewClosed(
            byte[] data,
            out RealtimeCorpseViewClosed closed,
            out string error)
        {
            closed = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.CorpseViewClosed,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadGuid(reader, out var corpseId, out error)
                    || !TryReadString(
                        reader,
                        MaximumErrorCodeLength,
                        out var code,
                        out error)
                    || !TryReadString(
                        reader,
                        MaximumErrorMessageLength,
                        out var message,
                        out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                if (corpseId == Guid.Empty)
                {
                    error = "Corpse view closure id is invalid.";
                    return false;
                }

                closed = new RealtimeCorpseViewClosed(corpseId, code, message);
                return true;
            }
        }

        private static bool IsValidCorpsePresenceChunk(
            RealtimeCorpsePresenceSnapshotChunk chunk)
        {
            return chunk != null
                && chunk.ChunkCount > 0
                && chunk.ChunkCount <= MaximumCorpseChunkCount
                && chunk.ChunkIndex < chunk.ChunkCount
                && chunk.Corpses != null
                && chunk.Corpses.Length <= MaximumCorpsePresencePerChunk
                && chunk.Corpses.All(IsValidCorpsePresence)
                && chunk.Corpses.Select(corpse => corpse.CorpseId).Distinct().Count()
                    == chunk.Corpses.Length;
        }

        private static bool IsValidCorpsePresence(RealtimeCorpsePresence corpse)
        {
            return corpse != null
                && corpse.CorpseId != Guid.Empty
                && IsFinite(corpse.PositionX)
                && IsFinite(corpse.PositionY)
                && IsFinite(corpse.PositionZ)
                && corpse.ExpiresAtUnixMilliseconds > 0
                && !string.IsNullOrWhiteSpace(corpse.SourceDisplayName)
                && corpse.SourceDisplayName.Length <= MaximumCorpseSourceNameLength
                && !string.IsNullOrWhiteSpace(corpse.PresentationKey)
                && corpse.PresentationKey.Length <= MaximumCorpsePresentationKeyLength;
        }

        private static bool IsValidCorpseInteractionIntent(
            RealtimeCorpseInteractionIntent intent)
        {
            if (intent == null
                || intent.OperationId == Guid.Empty
                || intent.CorpseId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeCorpseInteractionKind), intent.OperationKind))
            {
                return false;
            }

            if (intent.OperationKind == RealtimeCorpseInteractionKind.Open)
            {
                return intent.ExpectedCorpseRevision == -1;
            }

            if (intent.ExpectedCorpseRevision < 0)
            {
                return false;
            }

            if (intent.OperationKind == RealtimeCorpseInteractionKind.Close
                || intent.OperationKind == RealtimeCorpseInteractionKind.Refresh)
            {
                return true;
            }

            if (intent.OperationKind == RealtimeCorpseInteractionKind.LootItem
                || intent.OperationKind == RealtimeCorpseInteractionKind.LootPartialStack
                || intent.OperationKind == RealtimeCorpseInteractionKind.DepositItem
                || intent.OperationKind == RealtimeCorpseInteractionKind.DepositPartialStack
                || intent.OperationKind == RealtimeCorpseInteractionKind.MoveItem
                || intent.OperationKind == RealtimeCorpseInteractionKind.MovePartialStack)
            {
                var validTarget = intent.TargetItemInstanceId == Guid.Empty
                    ? intent.ExpectedTargetItemRevision == 0
                    : intent.ExpectedTargetItemRevision >= 0;
                return intent.ItemInstanceId != Guid.Empty
                    && intent.ExpectedItemRevision >= 0
                    && intent.DestinationContainerId != Guid.Empty
                    && intent.ExpectedDestinationContainerRevision >= 0
                    && intent.DestinationSlotIndex >= 0
                    && validTarget
                    && (intent.OperationKind == RealtimeCorpseInteractionKind.LootItem
                        || intent.OperationKind == RealtimeCorpseInteractionKind.DepositItem
                        || intent.OperationKind == RealtimeCorpseInteractionKind.MoveItem
                        ? intent.Quantity == 0
                        : intent.Quantity > 0);
            }

            return intent.OperationKind == RealtimeCorpseInteractionKind.SwapBag
                && intent.ItemInstanceId != Guid.Empty
                && intent.ExpectedItemRevision >= 0
                && intent.CorpseBagContentsContainerId != Guid.Empty
                && intent.ExpectedCorpseBagContentsRevision >= 0
                && intent.PlayerBagItemInstanceId != Guid.Empty
                && intent.ExpectedPlayerBagRevision >= 0
                && intent.PlayerBagContentsContainerId != Guid.Empty
                && intent.ExpectedPlayerBagContentsRevision >= 0
                && intent.ItemInstanceId != intent.PlayerBagItemInstanceId
                && intent.CorpseBagContentsContainerId != intent.PlayerBagContentsContainerId;
        }

        private static bool IsValidCorpseInteractionResult(
            RealtimeCorpseInteractionResult result)
        {
            if (result == null
                || result.OperationId == Guid.Empty
                || result.CorpseId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeCorpseInteractionKind), result.OperationKind)
                || !IsValidCarryState(result.CarryState))
            {
                return false;
            }

            if (result.Succeeded)
            {
                return result.Error == null && !result.RequiresCorpseRefresh;
            }

            return result.Error != null
                && !string.IsNullOrWhiteSpace(result.Error.Code)
                && result.Error.Code.Length <= MaximumErrorCodeLength
                && !string.IsNullOrWhiteSpace(result.Error.Message)
                && result.Error.Message.Length <= MaximumErrorMessageLength;
        }

        private static bool IsValidCorpseViewStateChunk(RealtimeCorpseViewStateChunk chunk)
        {
            if (chunk == null
                || chunk.UpdateId == Guid.Empty
                || !Enum.IsDefined(typeof(RealtimeCorpseViewUpdateKind), chunk.UpdateKind)
                || chunk.CorpseId == Guid.Empty
                || chunk.BaseRevision < -1
                || chunk.Revision < 0
                || chunk.Revision < chunk.BaseRevision
                || chunk.ExpiresAtUnixMilliseconds <= 0
                || string.IsNullOrWhiteSpace(chunk.SourceDisplayName)
                || chunk.SourceDisplayName.Length > MaximumCorpseSourceNameLength
                || string.IsNullOrWhiteSpace(chunk.PresentationKey)
                || chunk.PresentationKey.Length > MaximumCorpsePresentationKeyLength
                || chunk.ChunkCount == 0
                || chunk.ChunkCount > MaximumCorpseChunkCount
                || chunk.ChunkIndex >= chunk.ChunkCount
                || string.IsNullOrWhiteSpace(chunk.SectionKind)
                || chunk.SectionKind.Length > MaximumCorpseSectionKindLength
                || (chunk.SectionKind != "general_inventory"
                    && chunk.SectionKind != "equipment"
                    && chunk.SectionKind != "bag")
                || chunk.ContainerId == Guid.Empty
                || chunk.ContainerRevision < 0
                || chunk.SectionSlotCapacity <= 0
                || chunk.SectionSlotCapacity > MaximumCorpseSectionSlotCapacity
                || chunk.SlotOffset < 0
                || chunk.Slots == null
                || chunk.Slots.Length > MaximumCorpseViewSlotsPerChunk)
            {
                return false;
            }

            if (chunk.UpdateKind == RealtimeCorpseViewUpdateKind.Snapshot
                && (chunk.BaseRevision != -1 || !chunk.ReplaceSection))
            {
                return false;
            }

            var indexes = new HashSet<int>();
            foreach (var slot in chunk.Slots)
            {
                if (slot == null
                    || slot.SlotIndex < 0
                    || slot.SlotIndex >= chunk.SectionSlotCapacity
                    || !indexes.Add(slot.SlotIndex)
                    || string.IsNullOrWhiteSpace(slot.SlotKind)
                    || slot.SlotKind.Length > MaximumCorpseSlotKindLength
                    || slot.AcceptedTags == null
                    || slot.AcceptedTags.Length > MaximumSlotTags
                    || slot.AcceptedTags.Any(tag =>
                        string.IsNullOrWhiteSpace(tag)
                        || tag.Length > MaximumSlotTagLength)
                    || slot.AcceptedTags.Distinct(StringComparer.Ordinal).Count()
                        != slot.AcceptedTags.Length
                    || (chunk.SectionKind == "equipment")
                        != !string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    || slot.EquipmentSlotId.Length > MaximumEquipmentSlotIdLength
                    || !IsValidCorpseItem(slot.Item))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidCorpseItem(RealtimeCorpseItem item)
        {
            if (item == null)
            {
                return true;
            }

            var validBagReference = item.BagContentsContainerId == Guid.Empty
                ? item.BagContentsRevision == 0
                : item.BagContentsRevision >= 0;
            return item.ItemInstanceId != Guid.Empty
                && !string.IsNullOrWhiteSpace(item.DefinitionId)
                && item.DefinitionId.Length <= MaximumDefinitionIdLength
                && item.Quantity > 0
                && item.Revision >= 0
                && validBagReference;
        }
    }
}
