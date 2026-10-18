using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Actors
{
    // Actor content refers to the separately compiled item catalog: Mob loot names item
    // definitions, a vendor capability names a vendor profile, and a resource profile names a
    // tool kind and the definition it yields. The actor compiler checks the shapes; this checks
    // every reference against one catalog. SimulationWorker never loads the item catalog, and
    // GameService validates the same ids again when it commits a loot grant, a trade, or a
    // gathered yield.
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
            ValidateLoot(runtime, definitions, errors);
            ValidateVendors(runtime, itemCatalog, errors);
            ValidateResourceProfiles(runtime, itemCatalog, definitions, errors);
            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }

        private static void ValidateLoot(
            WorldActorRuntimeDocument runtime,
            IReadOnlyDictionary<string, ItemDefinition> definitions,
            ICollection<string> errors)
        {
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
        }

        private static void ValidateVendors(
            WorldActorRuntimeDocument runtime,
            ItemCatalogRuntimeDocument itemCatalog,
            ICollection<string> errors)
        {
            var vendorIds = new HashSet<string>(
                (itemCatalog.Vendors ?? Array.Empty<VendorProfileDefinition>()).Select(vendor => vendor.Id),
                StringComparer.Ordinal);
            foreach (var actor in runtime.Actors ?? Array.Empty<WorldActorDefinition>())
            {
                foreach (var capability in actor.Capabilities ?? Array.Empty<WorldActorCapabilityDefinition>())
                {
                    if (string.Equals(capability.Kind, WorldActorCapabilityKindIds.Vendor, StringComparison.Ordinal)
                        && !vendorIds.Contains(capability.ProfileId ?? string.Empty))
                    {
                        errors.Add(
                            "actor " + actor.Id + " capability " + capability.Id
                            + " references unknown vendor profile '" + capability.ProfileId + "'.");
                    }
                }
            }
        }

        private static void ValidateResourceProfiles(
            WorldActorRuntimeDocument runtime,
            ItemCatalogRuntimeDocument itemCatalog,
            IReadOnlyDictionary<string, ItemDefinition> definitions,
            ICollection<string> errors)
        {
            var toolKindIds = new HashSet<string>(
                (itemCatalog.ToolKinds ?? Array.Empty<ItemCatalogIdentityEntry>()).Select(kind => kind.Id),
                StringComparer.Ordinal);
            foreach (var profile in runtime.ResourceProfiles ?? Array.Empty<WorldActorResourceProfileDefinition>())
            {
                var path = "resource profile " + profile.Id;
                if (!toolKindIds.Contains(profile.RequiredToolKind ?? string.Empty))
                {
                    errors.Add(path + " references unknown tool kind '" + profile.RequiredToolKind + "'.");
                }
                else if (!definitions.Values.Any(definition =>
                             (definition.ToolKinds ?? Array.Empty<string>()).Contains(
                                 profile.RequiredToolKind,
                                 StringComparer.Ordinal)))
                {
                    errors.Add(path + " requires tool kind '" + profile.RequiredToolKind
                        + "', which no item definition provides.");
                }

                if (!definitions.TryGetValue(profile.YieldDefinitionId ?? string.Empty, out var yield))
                {
                    errors.Add(path + " yields unknown item definition '" + profile.YieldDefinitionId + "'.");
                    continue;
                }

                if (string.Equals(yield.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal)
                    || (yield.DefaultPolicies != null && yield.DefaultPolicies.Length > 0)
                    || (yield.Bag != null && yield.Bag.Slots != null && yield.Bag.Slots.Length > 0))
                {
                    errors.Add(path + " cannot yield a quest item, a Bag, or an item with default policies.");
                }

                if (profile.YieldQuantity > yield.MaximumStackSize)
                {
                    errors.Add(path + " yields more than one stack of '" + yield.Id + "'.");
                }
            }
        }
    }
}
