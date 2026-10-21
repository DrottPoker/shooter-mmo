using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // One change a predicted corpse operation shows in the corpse view: the new item of one
    // slot, or a whole section, such as the Bag contents that a Bag swap exchanges.
    public sealed class CorpseViewEdit
    {
        private CorpseViewEdit(Guid containerId, int slotIndex, CorpseLootItem item, CorpseLootSection section)
        {
            ContainerId = containerId;
            SlotIndex = slotIndex;
            Item = item;
            Section = section;
        }

        public Guid ContainerId { get; }

        public int SlotIndex { get; }

        public CorpseLootItem Item { get; }

        public CorpseLootSection Section { get; }

        public static CorpseViewEdit SetSlot(Guid containerId, int slotIndex, CorpseLootItem item)
        {
            return new CorpseViewEdit(containerId, slotIndex, item, null);
        }

        public static CorpseViewEdit ReplaceSection(CorpseLootSection section)
        {
            return new CorpseViewEdit(Guid.Empty, -1, null, section ?? throw new ArgumentNullException(nameof(section)));
        }

        // Applies the edits in order. An edit whose section or slot the view no longer has,
        // because a newer view replaced it, is skipped: the server's answer settles it.
        public static CorpseLootView Apply(CorpseLootView view, IEnumerable<CorpseViewEdit> edits)
        {
            if (view == null)
            {
                return null;
            }

            var sections = view.Sections.ToList();
            var changed = false;
            foreach (var edit in edits)
            {
                if (edit.Section != null)
                {
                    var index = sections.FindIndex(section =>
                        string.Equals(section.SectionKind, edit.Section.SectionKind, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        sections[index] = edit.Section;
                        changed = true;
                    }

                    continue;
                }

                var sectionIndex = sections.FindIndex(section => section.ContainerId == edit.ContainerId);
                if (sectionIndex < 0 || !sections[sectionIndex].TryGetSlot(edit.SlotIndex, out _))
                {
                    continue;
                }

                var source = sections[sectionIndex];
                sections[sectionIndex] = new CorpseLootSection(
                    source.SectionKind,
                    source.ContainerId,
                    source.ContainerRevision,
                    source.SlotCapacity,
                    source.Slots.Select(slot => slot.SlotIndex == edit.SlotIndex
                        ? new CorpseLootSlot(
                            slot.SlotIndex,
                            slot.SlotKind,
                            slot.AcceptedTags.ToArray(),
                            slot.EquipmentSlotId,
                            edit.Item)
                        : slot));
                changed = true;
            }

            return changed
                ? new CorpseLootView(
                    view.CorpseId,
                    view.Revision,
                    view.ExpiresAtUnixMilliseconds,
                    view.SourceDisplayName,
                    view.PresentationKey,
                    sections)
                : view;
        }
    }

    // The inventory side of predicted corpse operations, the way GameService commits them:
    // the client names the slot, a stack merges into a compatible stack, and a whole item that
    // cannot merge swaps with the item in the slot.
    internal static class CorpseInventoryPrediction
    {
        public static bool TryLoot(
            InventoryPredictionEdit edit,
            CorpseLootItem loot,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex,
            Guid createdItemId,
            out InventoryItem displaced,
            out string reason)
        {
            displaced = null;
            reason = edit.IsPredictedContainer(destinationContainerId)
                ? "Wait a moment until your equipped Bag is confirmed."
                : "The loot destination is no longer available.";
            var container = edit.FindContainer(destinationContainerId);
            if (container == null
                || !container.TryGetSlot(destinationSlotIndex, out _)
                || !edit.TryGetDefinition(loot.DefinitionId, out var definition))
            {
                return false;
            }

            var partial = quantity < loot.Quantity;
            var looted = new InventoryItem(createdItemId, loot.DefinitionId, quantity, 0, Array.Empty<InventoryPolicy>());
            edit.Editor.TryGetSlotItem(destinationContainerId, destinationSlotIndex, out var target);
            if (target == null)
            {
                edit.Editor.TryPlace(destinationContainerId, destinationSlotIndex, looted);
            }
            else if (InventoryTargetAdvisor.CanMerge(edit.Catalog, looted, target, out _))
            {
                edit.Editor.TryReplace(
                    target.ItemInstanceId,
                    InventoryPredictionEdit.WithQuantity(target, target.Quantity + quantity));
            }
            else if (!partial && edit.TryGetDefinition(target.DefinitionId, out var targetDefinition))
            {
                // The carried item swaps into the corpse slot the loot left.
                edit.Editor.Remove(target.ItemInstanceId);
                edit.Editor.TryPlace(destinationContainerId, destinationSlotIndex, looted);
                edit.MoveWeight(targetDefinition, target.Quantity, container.ContainerType, null);
                displaced = target;
            }
            else
            {
                reason = "The loot cannot merge with the stack in that slot.";
                return false;
            }

            edit.MoveWeight(definition, quantity, null, container.ContainerType);
            reason = "The loot would exceed the 140 percent carry cap.";
            return edit.FitsHardCap();
        }

        public static bool TryLootToEquipment(
            InventoryPredictionEdit edit,
            CorpseLootItem loot,
            string equipmentSlotId,
            Guid createdItemId,
            out InventoryItem displaced,
            out string reason)
        {
            displaced = null;
            reason = "The equipment slot is no longer available.";
            if (!edit.Editor.HasEquipmentSlot(equipmentSlotId))
            {
                return false;
            }

            var equipped = new InventoryItem(createdItemId, loot.DefinitionId, loot.Quantity, 0, Array.Empty<InventoryPolicy>());
            var occupant = edit.Editor.GetEquipped(equipmentSlotId);
            if (occupant == null)
            {
                return edit.Editor.TryEquip(equipmentSlotId, equipped);
            }

            if (InventoryTargetAdvisor.IsEquippedStackTopUp(edit.Catalog, equipped, occupant))
            {
                return edit.Editor.TryReplace(
                    occupant.ItemInstanceId,
                    InventoryPredictionEdit.WithQuantity(occupant, occupant.Quantity + loot.Quantity));
            }

            // The equipped item swaps into the corpse slot the loot came from.
            edit.Editor.Remove(occupant.ItemInstanceId);
            displaced = occupant;
            return edit.Editor.TryEquip(equipmentSlotId, equipped);
        }

        public static bool TryDeposit(
            InventoryPredictionEdit edit,
            Guid itemInstanceId,
            int quantity,
            CorpseLootItem swappedIn,
            out string reason)
        {
            reason = "The item is no longer in carried storage.";
            if (!edit.Editor.TryFind(itemInstanceId, out var item, out var source)
                || source.Kind != InventoryItemLocationKind.Container
                || quantity > item.Quantity
                || !edit.TryGetDefinition(item.DefinitionId, out var definition))
            {
                return false;
            }

            if (quantity < item.Quantity)
            {
                edit.Editor.TryReplace(item.ItemInstanceId, InventoryPredictionEdit.WithQuantity(item, item.Quantity - quantity));
            }
            else
            {
                edit.Editor.Remove(item.ItemInstanceId);
            }

            edit.MoveWeightFrom(definition, quantity, source, null);
            if (swappedIn == null)
            {
                return true;
            }

            // The corpse item that cannot merge swaps into the slot the deposit left.
            if (!edit.TryGetDefinition(swappedIn.DefinitionId, out var swappedDefinition)
                || !edit.Editor.TryPlace(source.ContainerId, source.SlotIndex, swappedIn.ToInventoryItem()))
            {
                reason = "The corpse item cannot take the slot the deposit left.";
                return false;
            }

            edit.MoveWeight(swappedDefinition, swappedIn.Quantity, null, source.ContainerType);
            reason = "The swap would exceed the 140 percent carry cap.";
            return edit.FitsHardCap();
        }

        // The corpse Bag, with its contents, takes the Bag slot, and the equipped Bag, with its
        // contents, goes into the corpse.
        public static bool TrySwapBag(
            InventoryPredictionEdit edit,
            CorpseLootItem corpseBag,
            CorpseLootSection corpseBagContents,
            out string reason)
        {
            reason = "Both Bags must still be in place.";
            var playerBag = edit.Editor.EquippedBag;
            var playerContents = edit.View.FullSnapshot.EquippedBag?.Contents;
            if (playerBag == null
                || playerContents == null
                || corpseBagContents == null
                || !edit.TryGetDefinition(playerBag.DefinitionId, out var playerDefinition)
                || !edit.TryGetDefinition(corpseBag.DefinitionId, out var corpseDefinition)
                || playerDefinition.Bag == null
                || corpseDefinition.Bag == null)
            {
                return false;
            }

            foreach (var slot in playerContents.Slots.Where(slot => slot.Item != null))
            {
                if (!edit.TryGetDefinition(slot.Item.DefinitionId, out var definition))
                {
                    return false;
                }

                edit.MoveWeight(definition, slot.Item.Quantity, ItemContainerTypeIds.BagContents, null);
            }

            var contents = new InventoryContainer(
                corpseBag.BagContentsContainerId,
                ItemContainerTypeIds.BagContents,
                corpseBag.BagContentsRevision,
                corpseBagContents.SlotCapacity,
                corpseBagContents.Slots
                    .Select(slot => new InventorySlot(
                        slot.SlotIndex,
                        slot.SlotKind,
                        slot.AcceptedTags.ToArray(),
                        slot.Item?.ToInventoryItem()))
                    .ToArray());
            foreach (var slot in contents.Slots.Where(slot => slot.Item != null))
            {
                if (!edit.TryGetDefinition(slot.Item.DefinitionId, out var definition))
                {
                    return false;
                }

                edit.MoveWeight(definition, slot.Item.Quantity, null, ItemContainerTypeIds.BagContents);
            }

            edit.Editor.Remove(playerBag.ItemInstanceId);
            edit.Capacity = edit.Capacity - playerDefinition.Bag.CarryCapacityBonus + corpseDefinition.Bag.CarryCapacityBonus;
            return edit.Editor.TryEquipBag(corpseBag.ToInventoryItem(), contents);
        }

        // The equipped Bag's contents as the corpse Bag section that the swap shows.
        public static CorpseLootSection ToCorpseSection(InventoryContainer contents)
        {
            return new CorpseLootSection(
                CorpseSectionKindIds.Bag,
                contents.ContainerId,
                contents.Revision,
                contents.SlotCapacity ?? contents.Slots.Count,
                contents.Slots.Select(slot => new CorpseLootSlot(
                    slot.SlotIndex,
                    slot.SlotKind,
                    slot.AcceptedTags.ToArray(),
                    slot.Item == null ? null : CorpseLootItem.FromInventoryItem(slot.Item))));
        }
    }
}
