using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Actors
{
    // Mob loot names item definitions from the separately compiled item catalog. The actor
    // compiler checks the loot shape; this checks every loot entry against one catalog.
    public static class WorldActorItemReferences
    {
        public static void Validate(
            WorldActorRuntimeDocument runtime,
            ItemCatalogRuntimeDocument itemCatalog)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (itemCatalog == null)
            {
                throw new ArgumentNullException(nameof(itemCatalog));
            }

            var definitions = (itemCatalog.Definitions ?? Array.Empty<ItemDefinition>())
                .Where(definition => definition?.Id != null)
                .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
            var errors = new List<string>();
            foreach (var profile in runtime.LootProfiles ?? Array.Empty<WorldActorLootProfileDefinition>())
            {
                foreach (var entry in profile.Entries ?? Array.Empty<WorldActorLootEntryDefinition>())
                {
                    var path = "lootProfiles '" + profile.Id + "' entry '" + entry.DefinitionId + "'";
                    if (!definitions.TryGetValue(entry.DefinitionId ?? string.Empty, out var definition))
                    {
                        errors.Add(path + " references an unknown item definition.");
                    }
                    else if (string.Equals(
                        definition.Category,
                        ItemCategoryIds.QuestItem,
                        StringComparison.Ordinal))
                    {
                        errors.Add(path + " cannot drop a quest item.");
                    }
                    else if (entry.MaximumQuantity > definition.MaximumStackSize)
                    {
                        errors.Add(path + " drops more than one stack of "
                            + definition.MaximumStackSize + " can hold.");
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }
    }
}
