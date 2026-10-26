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
            InventoryContainer bagContents,
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
            if (bagContents != null)
            {
                // A corpse Bag takes the empty Bag slot with what it holds.
                reason = "A corpse Bag needs an empty Bag slot.";
                return occupant == null && TryEquipBag(edit, equipped, bagContents, out reason);
            }
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

        // Equips a Bag with its contents, which become carried: its capacity bonus and their
        // weight count from now on.
        public static bool TryEquipBag(
            InventoryPredictionEdit edit,
            InventoryItem bag,
            InventoryContainer contents,
            out string reason)
        {
            reason = "The Bag cannot take the Bag slot.";
            if (!edit.TryGetDefinition(bag.DefinitionId, out var definition) || definition.Bag == null)
            {
                return false;
            }

            foreach (var slot in contents.Slots.Where(slot => slot.Item != null))
            {
                if (!edit.TryGetDefinition(slot.Item.DefinitionId, out var contentDefinition))
                {
                    return false;
                }

                edit.MoveWeight(contentDefinition, slot.Item.Quantity, null, ItemContainerTypeIds.BagContents);
            }

            edit.Capacity += definition.Bag.CarryCapacityBonus;
            if (!edit.Editor.TryEquipBag(bag, contents))
            {
                return false;
            }

            reason = "The Bag would exceed the 140 percent carry cap.";
            return edit.FitsHardCap();
        }

        // The contents of a corpse Bag as the Bag shows them once equipped: the corpse Bag
        // section for the corpse's equipped Bag, or the empty layout of any other Bag.
        public static InventoryContainer BagContents(CorpseLootView view, CorpseLootItem bag, ItemDefinition definition)
        {
            var section = view?.Sections.FirstOrDefault(candidate => candidate.ContainerId == bag.BagContentsContainerId);
            if (section == null)
            {
                return InventorySnapshotEditor.CreateBagContents(definition, bag.BagContentsContainerId, bag.BagContentsRevision);
            }

            return new InventoryContainer(
                bag.BagContentsContainerId,
                ItemContainerTypeIds.BagContents,
                bag.BagContentsRevision,
                section.SlotCapacity,
                section.Slots
                    .Select(slot => new InventorySlot(
                        slot.SlotIndex,
                        slot.SlotKind,
                        slot.AcceptedTags.ToArray(),
                        slot.Item?.ToInventoryItem()))
                    .ToArray());
        }

        // Whether the Bag is the corpse's equipped Bag, whose contents are the corpse Bag section.
        public static bool OwnsBagSection(CorpseLootView view, CorpseLootItem bag)
        {
            return bag != null
                && bag.HasBagContents
                && view != null
                && view.TryGetSection(CorpseSectionKindIds.Bag, out var section)
                && section.ContainerId == bag.BagContentsContainerId;
        }

        // The empty Bag section a corpse keeps once its equipped Bag left it. GameService names
        // the new container when it commits.
        public static CorpseViewEdit EmptyBagSection()
        {
            return CorpseViewEdit.ReplaceSection(new CorpseLootSection(
                CorpseSectionKindIds.Bag,
                Guid.NewGuid(),
                0,
                0,
                Array.Empty<CorpseLootSlot>()));
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

    // Loot all as GameService commits it: CorpseLootAllPlanner plans the steps from the shown
    // inventory and corpse, and the prediction applies them. The client does not know the
    // character level, so it shows equipment with a level requirement going into the inventory.
    internal static class CorpseLootAllPrediction
    {
        public static LootAllPlan Plan(IInventoryView view, CorpseLootView corpse, bool wholeItemsOnly)
        {
            var catalog = view.Catalog;
            var snapshot = view.FullSnapshot;
            var equipment = new List<LootAllEquipmentSlot>();
            foreach (var slot in snapshot.Equipment)
            {
                if (catalog.TryGetEquipmentSlot(slot.SlotId, out var entry))
                {
                    equipment.Add(new LootAllEquipmentSlot(entry, ToStack(catalog, slot.Item)));
                }
            }

            var bagContents = snapshot.EquippedBag?.Contents;
            var items = new List<LootAllCorpseItem>();
            foreach (var section in corpse.Sections)
            {
                foreach (var slot in section.Slots.Where(slot => slot.Item != null))
                {
                    var item = slot.Item;
                    LootAllBag bag = null;
                    if (item.HasBagContents
                        && catalog.TryGetDefinition(item.DefinitionId, out var bagDefinition)
                        && bagDefinition.Bag != null)
                    {
                        var contents = CorpseInventoryPrediction.BagContents(corpse, item, bagDefinition);
                        bag = new LootAllBag(bagDefinition.Bag.CarryCapacityBonus, ToCarriedSlots(catalog, contents));
                    }

                    items.Add(new LootAllCorpseItem(
                        item.ItemInstanceId,
                        item.DefinitionId,
                        item.Quantity,
                        section.SectionKind,
                        MergeKey(catalog, item.ToInventoryItem()),
                        true,
                        bag));
                }
            }

            return CorpseLootAllPlanner.Plan(new LootAllRequest(
                definitionId => catalog.TryGetDefinition(definitionId, out var definition) ? definition : null,
                definition => definition.RequiredCharacterLevel <= 1,
                snapshot.CarriedWeight,
                snapshot.CarryCapacity,
                equipment,
                ToCarriedSlots(catalog, snapshot.PermanentInventory),
                bagContents == null ? Array.Empty<LootAllCarriedSlot>() : ToCarriedSlots(catalog, bagContents),
                items,
                wholeItemsOnly));
        }

        // Applies the plan. Every item that arrives shows under the id the operation made for it
        // until GameService names its own; a stack it tops up keeps its id.
        public static bool TryApply(
            InventoryPredictionEdit edit,
            CorpseLootView corpse,
            bool wholeItemsOnly,
            IReadOnlyDictionary<Guid, Guid> createdItemIds,
            out string reason)
        {
            var plan = Plan(edit.View, corpse, wholeItemsOnly);
            reason = "Nothing in the corpse fits in your equipment or inventory.";
            if (plan.Steps.Count == 0)
            {
                return false;
            }

            reason = "The corpse or your inventory changed.";
            var arrived = new Dictionary<Guid, Guid>();
            var taken = new Dictionary<Guid, int>();
            foreach (var step in plan.Steps)
            {
                var source = step.Item;
                if (!createdItemIds.TryGetValue(source.ItemInstanceId, out var createdId)
                    || !edit.TryGetDefinition(source.DefinitionId, out var definition))
                {
                    return false;
                }

                var created = new InventoryItem(createdId, source.DefinitionId, step.Quantity, 0, Array.Empty<InventoryPolicy>());
                switch (step.Kind)
                {
                    case LootAllStepKind.EquipBag:
                        // A Bag whose contents earlier steps looted equips with what they left.
                        if (!corpse.TryFindItem(source.ItemInstanceId, out var bag, out _, out _)
                            || !edit.Editor.TryEquipBag(
                                created,
                                WithoutTaken(CorpseInventoryPrediction.BagContents(corpse, bag, definition), taken)))
                        {
                            return false;
                        }

                        arrived[source.ItemInstanceId] = createdId;
                        break;
                    case LootAllStepKind.Equip:
                        if (!edit.Editor.TryEquip(step.EquipmentSlotId, created))
                        {
                            return false;
                        }

                        arrived[source.ItemInstanceId] = createdId;
                        break;
                    case LootAllStepKind.TopUpEquipped:
                    case LootAllStepKind.Merge:
                    {
                        var targetId = arrived.TryGetValue(step.TargetItemInstanceId, out var mapped)
                            ? mapped
                            : step.TargetItemInstanceId;
                        if (!edit.Editor.TryFind(targetId, out var target, out _)
                            || !edit.Editor.TryReplace(targetId, InventoryPredictionEdit.WithQuantity(target, target.Quantity + step.Quantity)))
                        {
                            return false;
                        }

                        break;
                    }

                    case LootAllStepKind.Place:
                        if (!edit.Editor.TryPlace(step.Destination.ContainerId, step.Destination.SlotIndex, created))
                        {
                            return false;
                        }

                        if (step.TakesRest)
                        {
                            arrived[source.ItemInstanceId] = createdId;
                        }

                        break;
                    default:
                        return false;
                }

                taken[source.ItemInstanceId] = (taken.TryGetValue(source.ItemInstanceId, out var before) ? before : 0)
                    + step.Quantity;
            }

            edit.Weight = plan.CarriedWeight;
            edit.Capacity = plan.CarryCapacity;
            reason = string.Empty;
            return true;
        }

        // What the corpse shows once the plan committed: what is left of each looted item,
        // and an empty Bag section once its Bag left.
        public static IReadOnlyList<CorpseViewEdit> CorpseEdits(CorpseLootView corpse, LootAllPlan plan)
        {
            var edits = new List<CorpseViewEdit>();
            foreach (var steps in plan.Steps.GroupBy(step => step.Item.ItemInstanceId))
            {
                if (!corpse.TryFindItem(steps.Key, out var item, out var section, out var slot))
                {
                    continue;
                }

                var left = item.Quantity - steps.Sum(step => step.Quantity);
                edits.Add(CorpseViewEdit.SetSlot(section.ContainerId, slot.SlotIndex, left > 0 ? item.WithQuantity(left) : null));
                if (left <= 0 && CorpseInventoryPrediction.OwnsBagSection(corpse, item))
                {
                    edits.Add(CorpseInventoryPrediction.EmptyBagSection());
                }
            }

            return edits;
        }

        // Stacks merge when their policies and stack state match, as the merge rules compare
        // them, and only items whose policies allow stacking merge at all.
        public static string MergeKey(ClientItemCatalog catalog, InventoryItem item)
        {
            if (item == null
                || !catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !ItemPolicyRules.Evaluate(
                        definition,
                        item.Policies.Select(policy => new ItemPolicyState(
                            policy.Kind,
                            policy.Status,
                            policy.ProtectionSource,
                            string.Empty)))
                    .CanStack)
            {
                return null;
            }

            var policies = item.Policies.Count == 0
                ? "none"
                : string.Join(
                    "|",
                    item.Policies
                        .OrderBy(policy => policy.Kind, StringComparer.Ordinal)
                        .ThenBy(policy => policy.Status, StringComparer.Ordinal)
                        .Select(policy => policy.Kind + ":" + policy.Status));
            return policies + "\n" + definition.StructuralFingerprint;
        }

        // The contents less what earlier steps of the plan took out of them.
        private static InventoryContainer WithoutTaken(InventoryContainer contents, IReadOnlyDictionary<Guid, int> taken)
        {
            return new InventoryContainer(
                contents.ContainerId,
                contents.ContainerType,
                contents.Revision,
                contents.SlotCapacity ?? contents.Slots.Count,
                contents.Slots
                    .Select(slot =>
                    {
                        var item = slot.Item;
                        var left = item == null
                            ? 0
                            : item.Quantity - (taken.TryGetValue(item.ItemInstanceId, out var quantity) ? quantity : 0);
                        return new InventorySlot(
                            slot.SlotIndex,
                            slot.SlotKind,
                            slot.AcceptedTags.ToArray(),
                            left > 0 ? InventoryPredictionEdit.WithQuantity(item, left) : null);
                    })
                    .ToArray());
        }

        private static LootAllStack ToStack(ClientItemCatalog catalog, InventoryItem item)
        {
            return item == null
                ? null
                : new LootAllStack(item.ItemInstanceId, item.DefinitionId, item.Quantity, MergeKey(catalog, item));
        }

        private static LootAllCarriedSlot[] ToCarriedSlots(ClientItemCatalog catalog, InventoryContainer container)
        {
            return container.Slots
                .Select(slot => new LootAllCarriedSlot(
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    slot.SlotKind,
                    slot.AcceptedTags,
                    ToStack(catalog, slot.Item)))
                .ToArray();
        }
    }
}
