using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.Client.Quests
{
    // Whether the rewards of a turn-in fit, by the rules GameService places them with: the
    // collect objectives take their items and the quest's other quest items leave, then each
    // reward item tops up carried stacks without a policy and takes the free slots that accept
    // it, matching specialized Bag slots, the permanent inventory, then general Bag slots. The
    // carried weight must end within the hard cap and the balance within its maximum. Which
    // stacks a collect objective empties is GameService's choice, so every slot that one could
    // empty counts as free for any reward: the check refuses only what GameService refuses too,
    // and GameService decides the rest.
    public static class QuestRewardFit
    {
        public static bool Fits(
            QuestDefinition quest,
            string rewardChoiceDefinitionId,
            CharacterInventorySnapshot snapshot,
            ClientItemCatalog catalog,
            out string reason)
        {
            reason = string.Empty;
            if (quest?.Steps == null
                || quest.Steps.Length == 0
                || snapshot?.PermanentInventory == null
                || catalog == null)
            {
                return true;
            }

            if (quest.RewardGold > VendorPricing.MaximumCurrencyBalance - snapshot.Currency)
            {
                reason = "Your balance cannot hold the Gold reward.";
                return false;
            }

            var rewards = (quest.RewardItems ?? Array.Empty<QuestItemQuantityDefinition>())
                .Concat((quest.RewardChoiceItems ?? Array.Empty<QuestItemQuantityDefinition>())
                    .Where(choice => string.Equals(choice.DefinitionId, rewardChoiceDefinitionId, StringComparison.Ordinal)))
                .ToArray();
            if (rewards.Length == 0)
            {
                return true;
            }

            var slots = CarriedSlots(snapshot);
            var questItemIds = QuestItemIds(quest, catalog);
            var removedWeight = 0L;
            var addedWeight = 0L;
            try
            {
                foreach (var slot in slots.Where(slot => !slot.IsEmpty && slot.QuestGrant && questItemIds.Contains(slot.DefinitionId)))
                {
                    removedWeight = checked(removedWeight + WeightOf(catalog, slot.DefinitionId, slot.Quantity));
                    slot.Empty();
                }

                foreach (var objective in quest.Steps[quest.Steps.Length - 1].Objectives
                             .Where(objective => string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal)
                                 && !questItemIds.Contains(objective.ItemId)))
                {
                    removedWeight = checked(removedWeight + TakeAsTheBestCase(slots, catalog, objective));
                }

                foreach (var reward in rewards)
                {
                    if (!catalog.TryGetDefinition(reward.DefinitionId, out var definition))
                    {
                        return true;
                    }

                    addedWeight = checked(addedWeight + ItemWeightRules.CalculateStackWeight(definition.UnitWeight, reward.Quantity));
                    if (!Place(slots, definition, reward.Quantity))
                    {
                        reason = "Make room in your bags for the rewards first.";
                        return false;
                    }
                }

                var weight = Math.Max(0L, checked(snapshot.CarriedWeight - removedWeight + addedWeight));
                if (!EncumbranceRules.IsAllowedVoluntaryTransition(
                        snapshot.CarriedWeight,
                        snapshot.CarryCapacity,
                        weight,
                        snapshot.CarryCapacity))
                {
                    reason = "The rewards would be too heavy to carry.";
                    return false;
                }
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                // Weights beyond what GameService stores; GameService decides.
                return true;
            }

            return true;
        }

        // The definitions of the quest's quest items: its start items, and the items its
        // objectives collect or deliver that are quest items. A quest item belongs to one quest,
        // so every quest grant of these carries this quest's lineage, which the turn-in removes.
        private static HashSet<string> QuestItemIds(QuestDefinition quest, ClientItemCatalog catalog)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in quest.StartItems ?? Array.Empty<QuestItemQuantityDefinition>())
            {
                if (!string.IsNullOrEmpty(item?.DefinitionId))
                {
                    ids.Add(item.DefinitionId);
                }
            }

            foreach (var objective in quest.Steps.SelectMany(step => step.Objectives ?? Array.Empty<QuestObjectiveDefinition>()))
            {
                if (!string.IsNullOrEmpty(objective.ItemId)
                    && catalog.TryGetDefinition(objective.ItemId, out var definition)
                    && string.Equals(definition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal))
                {
                    ids.Add(objective.ItemId);
                }
            }

            return ids;
        }

        // Takes the objective's count from stacks without a policy, the smallest first, which
        // empties as many slots as any order can; each emptied slot may take any reward.
        private static long TakeAsTheBestCase(List<CarriedSlot> slots, ClientItemCatalog catalog, QuestObjectiveDefinition objective)
        {
            var remaining = objective.Count;
            var weight = 0L;
            foreach (var slot in slots
                         .Where(slot => slot.PolicyFree && string.Equals(slot.DefinitionId, objective.ItemId, StringComparison.Ordinal))
                         .OrderBy(slot => slot.Quantity))
            {
                if (remaining <= 0)
                {
                    break;
                }

                var taken = Math.Min(remaining, slot.Quantity);
                weight = checked(weight + WeightOf(catalog, slot.DefinitionId, taken));
                slot.Quantity -= taken;
                remaining -= taken;
                if (slot.Quantity == 0)
                {
                    slot.Empty();
                    slot.TakesAnyItem = true;
                }
            }

            return weight;
        }

        // Tops up stacks without a policy, the permanent inventory first, and puts the rest into
        // free slots in GameService's order, then into the slots a collect objective may empty.
        private static bool Place(List<CarriedSlot> slots, ItemDefinition definition, int quantity)
        {
            var remaining = quantity;
            if (definition.MaximumStackSize > 1)
            {
                foreach (var slot in slots
                             .Where(slot => slot.PolicyFree
                                 && string.Equals(slot.DefinitionId, definition.Id, StringComparison.Ordinal)
                                 && slot.Quantity < definition.MaximumStackSize)
                             .OrderBy(slot => slot.InBag ? 1 : 0)
                             .ThenBy(slot => slot.Source.SlotIndex))
                {
                    var added = Math.Min(remaining, definition.MaximumStackSize - slot.Quantity);
                    slot.Quantity += added;
                    remaining -= added;
                    if (remaining == 0)
                    {
                        return true;
                    }
                }
            }

            while (remaining > 0)
            {
                var free = slots
                    .Where(slot => slot.IsEmpty
                        && (slot.TakesAnyItem || InventoryTargetAdvisor.SlotAccepts(definition, slot.Container, slot.Source)))
                    .OrderBy(slot => slot.TakesAnyItem ? 1 : 0)
                    .ThenBy(slot => slot.FillOrder)
                    .ThenBy(slot => slot.Source.SlotIndex)
                    .FirstOrDefault();
                if (free == null)
                {
                    return false;
                }

                var placed = Math.Min(remaining, Math.Max(1, definition.MaximumStackSize));
                free.Fill(definition.Id, placed);
                remaining -= placed;
            }

            return true;
        }

        private static long WeightOf(ClientItemCatalog catalog, string definitionId, int quantity)
        {
            return catalog.TryGetDefinition(definitionId, out var definition)
                ? ItemWeightRules.CalculateStackWeight(definition.UnitWeight, quantity)
                : 0L;
        }

        private static List<CarriedSlot> CarriedSlots(CharacterInventorySnapshot snapshot)
        {
            var slots = snapshot.PermanentInventory.Slots
                .Select(slot => new CarriedSlot(snapshot.PermanentInventory, slot, false))
                .ToList();
            var bag = snapshot.EquippedBag?.Contents;
            if (bag != null)
            {
                slots.AddRange(bag.Slots.Select(slot => new CarriedSlot(bag, slot, true)));
            }

            return slots;
        }

        private sealed class CarriedSlot
        {
            public CarriedSlot(InventoryContainer container, InventorySlot source, bool inBag)
            {
                Container = container;
                Source = source;
                InBag = inBag;
                var item = source.Item;
                if (item != null)
                {
                    DefinitionId = item.DefinitionId;
                    Quantity = item.Quantity;
                    PolicyFree = !item.Policies.Any(QuestCarriedItems.IsActive);
                    QuestGrant = QuestCarriedItems.IsQuestGrant(item);
                }
            }

            public InventoryContainer Container { get; }

            public InventorySlot Source { get; }

            public bool InBag { get; }

            // GameService fills matching specialized Bag slots, then the permanent inventory,
            // then general Bag slots.
            public int FillOrder
            {
                get
                {
                    if (!InBag)
                    {
                        return 1;
                    }

                    return string.Equals(Source.SlotKind, BagSlotKindIds.Specialized, StringComparison.Ordinal) ? 0 : 2;
                }
            }

            public string DefinitionId { get; private set; }

            public int Quantity { get; set; }

            public bool PolicyFree { get; private set; }

            public bool QuestGrant { get; private set; }

            // A slot that a collect objective may empty, which may take any reward.
            public bool TakesAnyItem { get; set; }

            public bool IsEmpty
            {
                get { return DefinitionId == null; }
            }

            public void Empty()
            {
                DefinitionId = null;
                Quantity = 0;
                PolicyFree = false;
                QuestGrant = false;
            }

            // A new reward stack, which carries no policy.
            public void Fill(string definitionId, int quantity)
            {
                DefinitionId = definitionId;
                Quantity = quantity;
                PolicyFree = true;
                QuestGrant = false;
                TakesAnyItem = false;
            }
        }
    }
}
