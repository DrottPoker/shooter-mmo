using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Actors
{
    // Actor content refers to the separately compiled item catalog: Mob loot names item
    // definitions, a vendor capability names a vendor profile, and a resource profile names a
    // tool kind and the definition it yields. The actor compiler checks the shapes; this checks
    // every reference of the actor catalog against one item catalog. Only the actor catalog
    // refers to items, so it runs once per catalog, not per World: the actor compiler runs it
    // against the compiled item catalog, and SimulationWorker at startup against the item
    // catalog it loads.
    public static class WorldActorItemReferences
    {
        public static void Validate(
            WorldActorCatalogRuntimeDocument catalog,
            ItemCatalogRuntimeDocument itemCatalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (itemCatalog == null)
            {
                throw new ArgumentNullException(nameof(itemCatalog));
            }

            var definitions = (itemCatalog.Definitions ?? Array.Empty<ItemDefinition>())
                .Where(definition => definition?.Id != null)
                .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
            var errors = new List<string>();
            ValidateLoot(catalog, definitions, errors);
            ValidateVendors(catalog, itemCatalog, errors);
            ValidateResourceProfiles(catalog, itemCatalog, definitions, errors);
            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }

        private static void ValidateLoot(
            WorldActorCatalogRuntimeDocument catalog,
            IReadOnlyDictionary<string, ItemDefinition> definitions,
            ICollection<string> errors)
        {
            foreach (var profile in catalog.LootProfiles ?? Array.Empty<WorldActorLootProfileDefinition>())
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
            WorldActorCatalogRuntimeDocument catalog,
            ItemCatalogRuntimeDocument itemCatalog,
            ICollection<string> errors)
        {
            var vendorIds = new HashSet<string>(
                (itemCatalog.Vendors ?? Array.Empty<VendorProfileDefinition>()).Select(vendor => vendor.Id),
                StringComparer.Ordinal);
            foreach (var actor in catalog.Actors ?? Array.Empty<WorldActorDefinition>())
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
            WorldActorCatalogRuntimeDocument catalog,
            ItemCatalogRuntimeDocument itemCatalog,
            IReadOnlyDictionary<string, ItemDefinition> definitions,
            ICollection<string> errors)
        {
            var toolKindIds = new HashSet<string>(
                (itemCatalog.ToolKinds ?? Array.Empty<ItemCatalogIdentityEntry>()).Select(kind => kind.Id),
                StringComparer.Ordinal);
            foreach (var profile in catalog.ResourceProfiles ?? Array.Empty<WorldActorResourceProfileDefinition>())
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
