using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // Applies one committed OwnerItemStateChanged to the snapshot it follows. Anything the
    // change cannot describe exactly, such as an equipped Bag's contents or Recovery Storage,
    // refuses the patch, and the caller reloads the inventory instead.
    public static class InventoryItemStateChangePatch
    {
        public static bool TryApply(
            ClientItemCatalog catalog,
            CharacterInventorySnapshot snapshot,
            InventoryContainer bank,
            RecoveryStorageInventory recovery,
            RealtimeOwnerItemStateChanged change,
            out CharacterInventorySnapshot patched)
        {
            patched = null;
            if (catalog == null
                || snapshot == null
                || change?.CarryState == null
                || change.PreviousItemStateRevision != snapshot.ItemStateRevision
                || change.CarryState.ItemStateRevision != snapshot.ItemStateRevision + 1
                || change.CarryState.CarryCapacity <= 0
                || change.CarryState.CarriedWeight < 0)
            {
                return false;
            }

            var editor = new InventorySnapshotEditor(snapshot, bank, recovery);
            var recoveryContainerId = editor.RecoveryStorageContainerId;
            // Every changed item leaves its slot first, so swaps and moves into a slot another
            // changed item left place cleanly.
            foreach (var item in change.Items)
            {
                if (editor.IsInRecoveryStorage(item.ItemInstanceId)
                    || (editor.TryFind(item.ItemInstanceId, out _, out var location)
                        && IsEquippedBagSlot(location)))
                {
                    return false;
                }

                editor.Remove(item.ItemInstanceId);
            }

            foreach (var item in change.Items)
            {
                if (!TryPlace(catalog, editor, recoveryContainerId, item))
                {
                    return false;
                }
            }

            var revisedContainerIds = new HashSet<Guid>();
            foreach (var revision in change.ContainerRevisions)
            {
                if (editor.SetContainerRevision(revision.ContainerId, revision.Revision))
                {
                    revisedContainerIds.Add(revision.ContainerId);
                }
            }

            if (!editor.ChangedContainersHaveRevisions(revisedContainerIds))
            {
                return false;
            }

            patched = editor.Build(
                change.CarryState.ItemStateRevision,
                change.CarryState.CarriedWeight,
                change.CarryState.CarryCapacity,
                change.Currency);
            return true;
        }

        private static bool TryPlace(
            ClientItemCatalog catalog,
            InventorySnapshotEditor editor,
            Guid recoveryContainerId,
            RealtimeItemStateChange item)
        {
            switch (item.Location)
            {
                case RealtimeItemStateLocationKind.Removed:
                    return true;
                case RealtimeItemStateLocationKind.Container:
                    if (item.ContainerId == recoveryContainerId)
                    {
                        return false;
                    }

                    // A container the snapshot does not show, such as a corpse or the contents of
                    // a Bag in storage, takes the item out of view.
                    return !editor.HasContainer(item.ContainerId)
                        || (catalog.TryGetDefinition(item.DefinitionId, out _)
                            && editor.TryPlace(item.ContainerId, item.SlotIndex, ToItem(item)));
                default:
                    return !string.Equals(item.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
                        && catalog.TryGetDefinition(item.DefinitionId, out _)
                        && editor.TryEquip(item.EquipmentSlotId, ToItem(item));
            }
        }

        // An equipped Bag brings its contents, which only a reload can show.
        private static bool IsEquippedBagSlot(InventoryItemLocation location)
        {
            return location.Kind == InventoryItemLocationKind.Equipment
                && string.Equals(location.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal);
        }

        private static InventoryItem ToItem(RealtimeItemStateChange item)
        {
            return new InventoryItem(
                item.ItemInstanceId,
                item.DefinitionId,
                item.Quantity,
                item.Revision,
                item.Policies
                    .Select(policy => new InventoryPolicy(policy.Kind, policy.Status, policy.ProtectionSource))
                    .ToArray());
        }
    }
}
