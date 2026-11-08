using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Quests
{
    // Quest content refers to the separately compiled item catalog. The quest compiler checks every
    // reference against the catalog it compiled with; this checks a loaded quest catalog against
    // the item catalog that a service loads, so no service runs a quest whose items it does not
    // have in the form the quest needs. GameService and SimulationWorker run it at startup.
    public static class QuestItemReferences
    {
        public static void Validate(IEnumerable<QuestDefinition> quests, ItemCatalogRuntimeDocument itemCatalog)
        {
            if (quests == null)
            {
                throw new ArgumentNullException(nameof(quests));
            }

            if (itemCatalog == null)
            {
                throw new ArgumentNullException(nameof(itemCatalog));
            }

            var definitions = (itemCatalog.Definitions ?? Array.Empty<ItemDefinition>())
                .Where(definition => definition?.Id != null)
                .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
            var errors = new List<string>();
            foreach (var quest in quests)
            {
                var path = "quest " + quest.Id;
                foreach (var item in quest.StartItems)
                {
                    ValidateQuantity(path + " start item", item, definitions, IsQuestDrop, errors);
                }

                foreach (var item in quest.RewardItems.Concat(quest.RewardChoiceItems))
                {
                    ValidateQuantity(path + " reward", item, definitions, IsReward, errors);
                }

                foreach (var objective in quest.Steps.SelectMany(step => step.Objectives))
                {
                    if (!string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal)
                        && !string.Equals(objective.Kind, QuestObjectiveKindIds.Deliver, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var objectivePath = path + " objective " + objective.Id;
                    if (!definitions.TryGetValue(objective.ItemId, out var definition))
                    {
                        errors.Add(objectivePath + " names unknown item definition '" + objective.ItemId + "'.");
                        continue;
                    }

                    if (!IsQuestItem(definition) && (definition.DefaultPolicies ?? Array.Empty<string>()).Length > 0)
                    {
                        errors.Add(objectivePath + " names '" + definition.Id + "', which has default policies and would never count.");
                    }

                    foreach (var source in objective.DropSources)
                    {
                        if (!IsQuestDrop(definition) || source.Quantity > definition.MaximumStackSize)
                        {
                            errors.Add(objectivePath + " drops '" + definition.Id
                                + "', which must be a protected quest item that cannot be equipped, at most one stack.");
                        }
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new QuestValidationException(errors);
            }
        }

        private static void ValidateQuantity(
            string path,
            QuestItemQuantityDefinition item,
            IReadOnlyDictionary<string, ItemDefinition> definitions,
            Func<ItemDefinition, bool> accepts,
            ICollection<string> errors)
        {
            if (!definitions.TryGetValue(item.DefinitionId, out var definition))
            {
                errors.Add(path + " names unknown item definition '" + item.DefinitionId + "'.");
            }
            else if (!accepts(definition) || item.Quantity > definition.MaximumStackSize)
            {
                errors.Add(path + " '" + definition.Id + "' is the wrong kind of item or more than one stack.");
            }
        }

        private static bool IsQuestItem(ItemDefinition definition)
        {
            return string.Equals(definition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal);
        }

        // Start items and quest drops carry the quest's lineage, which only a protected quest item
        // keeps, and they never go into equipment.
        private static bool IsQuestDrop(ItemDefinition definition)
        {
            return IsQuestItem(definition)
                && (definition.DefaultPolicies ?? Array.Empty<string>()).Contains(ItemPolicyIds.ProtectedOnDeath)
                && (definition.EquipmentSlots ?? Array.Empty<string>()).Length == 0;
        }

        // A reward is an ordinary item that carried acquisition can place.
        private static bool IsReward(ItemDefinition definition)
        {
            return !IsQuestItem(definition) && (definition.DefaultPolicies ?? Array.Empty<string>()).Length == 0;
        }
    }
}
