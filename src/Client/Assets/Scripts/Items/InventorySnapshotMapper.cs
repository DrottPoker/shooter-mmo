using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public static class InventorySnapshotMapper
    {
        public static bool TryMap(
            CharacterInventorySnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterInventorySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapContainer(
                    response.permanentInventory,
                    catalog,
                    out var permanent,
                    out error)
                || !TryMapEquipment(response.equipment, catalog, out var equipment, out error)
                || !TryMapEquippedBag(response.equippedBag, catalog, out var bag, out error)
                || !TryMapContainer(response.bank, catalog, out var bank, out error)
                || !TryMapRecovery(response.recoveryStorage, catalog, out var recovery, out error))
            {
                return false;
            }

            if (response.carriedWeight < 0
                || response.carryCapacity <= 0
                || response.loadRatioBasisPoints < 0
                || response.movementMultiplierBasisPoints < 0)
            {
                error = "The inventory snapshot contains invalid carry state.";
                return false;
            }

            var expectedLoadRatio = CalculateLoadRatioBasisPoints(
                response.carriedWeight,
                response.carryCapacity);
            var expectedMovementMultiplier = EncumbranceRules
                .CalculateMovementMultiplierBasisPoints(
                    response.carriedWeight,
                    response.carryCapacity);
            // Weight above the hard cap is valid after an involuntary death overflow.
            if (response.loadRatioBasisPoints != expectedLoadRatio
                || response.movementMultiplierBasisPoints != expectedMovementMultiplier)
            {
                error = "The inventory snapshot contains inconsistent carry state.";
                return false;
            }

            if (response.currency < 0
                || response.currency > VendorPricing.MaximumCurrencyBalance)
            {
                error = "The inventory snapshot contains an invalid currency balance.";
                return false;
            }

            snapshot = new CharacterInventorySnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                permanent,
                equipment,
                bag,
                bank,
                recovery,
                response.carriedWeight,
                response.carryCapacity,
                response.loadRatioBasisPoints,
                response.movementMultiplierBasisPoints,
                response.currency);
            return true;
        }

        public static bool TryMap(
            CharacterBankSnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterBankSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapContainer(response.bank, catalog, out var bank, out error))
            {
                return false;
            }

            snapshot = new CharacterBankSnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                bank);
            return true;
        }

        public static bool TryMap(
            CharacterRecoverySnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterRecoverySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapRecovery(
                    response.recoveryStorage,
                    catalog,
                    out var recovery,
                    out error))
            {
                return false;
            }

            snapshot = new CharacterRecoverySnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                recovery);
            return true;
        }

        private static bool TryMapContainer(
            ItemContainerSnapshotResponse response,
            ClientItemCatalog catalog,
            out InventoryContainer container,
            out string error)
        {
            container = null;
            error = string.Empty;
            if (response == null
                || !Guid.TryParse(response.containerId, out var containerId)
                || containerId == Guid.Empty
                || string.IsNullOrWhiteSpace(response.containerType)
                || response.revision < 0)
            {
                error = "The inventory snapshot contains an invalid container.";
                return false;
            }

            var sourceSlots = response.slots ?? Array.Empty<ItemSlotSnapshotResponse>();
            if (sourceSlots.Any(slot => slot == null || slot.slotIndex < 0)
                || sourceSlots.Select(slot => slot.slotIndex).Distinct().Count()
                    != sourceSlots.Length)
            {
                error = "The inventory snapshot contains invalid or duplicate slots.";
                return false;
            }

            var slots = new List<InventorySlot>();
            foreach (var sourceSlot in sourceSlots.OrderBy(slot => slot.slotIndex))
            {
                if (!TryMapItem(sourceSlot.item, catalog, out var item, out error))
                {
                    return false;
                }

                slots.Add(new InventorySlot(
                    sourceSlot.slotIndex,
                    string.IsNullOrWhiteSpace(sourceSlot.slotKind)
                        ? BagSlotKindIds.General
                        : sourceSlot.slotKind,
                    sourceSlot.acceptedTags ?? Array.Empty<string>(),
                    item));
            }

            container = new InventoryContainer(
                containerId,
                response.containerType,
                response.revision,
                response.slotCapacity > 0 ? response.slotCapacity : (int?)null,
                slots.ToArray());
            return true;
        }

        private static bool TryMapEquipment(
            EquipmentSlotSnapshotResponse[] responses,
            ClientItemCatalog catalog,
            out InventoryEquipmentSlot[] equipment,
            out string error)
        {
            error = string.Empty;
            var source = responses ?? Array.Empty<EquipmentSlotSnapshotResponse>();
            if (source.Any(slot => slot == null || string.IsNullOrWhiteSpace(slot.equipmentSlotId))
                || source.Select(slot => slot.equipmentSlotId)
                    .Distinct(StringComparer.Ordinal).Count() != source.Length)
            {
                equipment = null;
                error = "The inventory snapshot contains invalid equipment slots.";
                return false;
            }

            var result = new List<InventoryEquipmentSlot>();
            foreach (var slot in source.OrderBy(value => value.sortOrder))
            {
                if (!catalog.TryGetEquipmentSlot(slot.equipmentSlotId, out _)
                    || !TryMapItem(slot.item, catalog, out var item, out error))
                {
                    equipment = null;
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = "The inventory snapshot references an unknown equipment slot.";
                    }

                    return false;
                }

                result.Add(new InventoryEquipmentSlot(
                    slot.equipmentSlotId,
                    slot.sortOrder,
                    item));
            }

            equipment = result.ToArray();
            return true;
        }

        private static bool TryMapEquippedBag(
            EquippedBagSnapshotResponse response,
            ClientItemCatalog catalog,
            out EquippedBagInventory bag,
            out string error)
        {
            bag = null;
            error = string.Empty;
            if (response == null)
            {
                return true;
            }

            if (!TryMapItem(response.item, catalog, out var item, out error))
            {
                return false;
            }

            if (item == null)
            {
                if (IsJsonNullPlaceholder(response.contents))
                {
                    return true;
                }

                error = "The equipped Bag snapshot is missing its Bag item.";
                return false;
            }

            if (!TryMapContainer(response.contents, catalog, out var contents, out error))
            {
                return false;
            }

            if (!catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || definition.Bag == null)
            {
                error = "The equipped Bag snapshot does not reference a Bag definition.";
                return false;
            }

            bag = new EquippedBagInventory(item, contents);
            return true;
        }

        private static bool TryMapRecovery(
            RecoveryStorageSnapshotResponse response,
            ClientItemCatalog catalog,
            out RecoveryStorageInventory recovery,
            out string error)
        {
            recovery = null;
            error = string.Empty;
            if (response == null
                || !Guid.TryParse(response.containerId, out var containerId)
                || containerId == Guid.Empty
                || response.revision < 0)
            {
                error = "The inventory snapshot contains invalid Recovery Storage.";
                return false;
            }

            var source = response.deliveries ?? Array.Empty<RecoveryDeliverySnapshotResponse>();
            var deliveries = new List<RecoveryDelivery>();
            var deliveryIds = new HashSet<Guid>();
            foreach (var delivery in source)
            {
                if (delivery == null
                    || !Guid.TryParse(delivery.deliveryId, out var deliveryId)
                    || deliveryId == Guid.Empty
                    || !deliveryIds.Add(deliveryId)
                    || delivery.revision < 0
                    || string.IsNullOrWhiteSpace(delivery.sourceKind))
                {
                    error = "The inventory snapshot contains an invalid Recovery delivery.";
                    return false;
                }

                var items = new List<RecoveryDeliveryItem>();
                foreach (var responseItem in delivery.items
                    ?? Array.Empty<RecoveryDeliveryItemSnapshotResponse>())
                {
                    if (responseItem == null
                        || responseItem.itemOrder < 0
                        || responseItem.containerSlotIndex < 0
                        || !TryMapItem(responseItem.item, catalog, out var item, out error)
                        || item == null)
                    {
                        if (string.IsNullOrWhiteSpace(error))
                        {
                            error = "The Recovery delivery contains an invalid item.";
                        }

                        return false;
                    }

                    items.Add(new RecoveryDeliveryItem(
                        responseItem.itemOrder,
                        responseItem.containerSlotIndex,
                        item));
                }

                deliveries.Add(new RecoveryDelivery(
                    deliveryId,
                    delivery.revision,
                    delivery.sourceKind,
                    delivery.createdAt,
                    delivery.availableAt,
                    delivery.expiresAt,
                    delivery.claimedAt,
                    items.OrderBy(item => item.ItemOrder).ToArray()));
            }

            recovery = new RecoveryStorageInventory(
                containerId,
                response.revision,
                deliveries.ToArray());
            return true;
        }

        private static bool TryMapItem(
            ItemInstanceSnapshotResponse response,
            ClientItemCatalog catalog,
            out InventoryItem item,
            out string error)
        {
            item = null;
            error = string.Empty;
            if (response == null || IsJsonNullPlaceholder(response))
            {
                return true;
            }

            if (!Guid.TryParse(response.itemInstanceId, out var itemId)
                || itemId == Guid.Empty)
            {
                error = "The inventory snapshot contains an item instance with an invalid id.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.definitionId)
                || !catalog.TryGetDefinition(response.definitionId, out _))
            {
                error = "The inventory snapshot references an unknown item definition.";
                return false;
            }

            if (response.quantity <= 0)
            {
                error = "The inventory snapshot contains a non-positive item quantity.";
                return false;
            }

            if (response.revision < 0)
            {
                error = "The inventory snapshot contains a negative item revision.";
                return false;
            }

            var policies = (response.policies ?? Array.Empty<ItemPolicySummaryResponse>())
                .Where(policy => policy != null)
                .Select(policy => new InventoryPolicy(
                    policy.policyKind ?? string.Empty,
                    policy.status ?? string.Empty,
                    policy.protectionSource ?? string.Empty))
                .ToArray();
            item = new InventoryItem(
                itemId,
                response.definitionId,
                response.quantity,
                response.revision,
                policies);
            return true;
        }

        private static bool IsJsonNullPlaceholder(ItemInstanceSnapshotResponse response)
        {
            return response != null
                && string.IsNullOrEmpty(response.itemInstanceId)
                && string.IsNullOrEmpty(response.definitionId)
                && response.quantity == 0
                && response.revision == 0
                && (response.policies == null || response.policies.Length == 0);
        }

        private static bool IsJsonNullPlaceholder(ItemContainerSnapshotResponse response)
        {
            return response == null
                || (string.IsNullOrEmpty(response.containerId)
                    && string.IsNullOrEmpty(response.containerType)
                    && response.revision == 0
                    && response.slotCapacity == 0
                    && (response.slots == null || response.slots.Length == 0));
        }

        private static bool ValidateCatalog(
            string revision,
            ClientItemCatalog catalog,
            out string error)
        {
            if (catalog == null || !catalog.MatchesServerRevision(revision))
            {
                error = "The server item catalog revision does not match this client build.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool ValidateItemStateRevision(long revision, out string error)
        {
            if (revision < 0)
            {
                error = "The inventory snapshot contains an invalid item-state revision.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryCharacterId(
            string value,
            out Guid characterId,
            out string error)
        {
            if (!Guid.TryParse(value, out characterId) || characterId == Guid.Empty)
            {
                error = "The inventory snapshot contains an invalid character id.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static int CalculateLoadRatioBasisPoints(long carriedWeight, long capacity)
        {
            var scaled = ((System.Numerics.BigInteger)carriedWeight * 10_000) / capacity;
            return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
        }
    }
}
