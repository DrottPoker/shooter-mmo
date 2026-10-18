using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.WorldData.Actors
{
    public static partial class WorldActorCompiler
    {
        // Actor content refers to item content: a vendor capability names a vendor profile, and
        // a resource profile names a tool kind and the definition it yields. The item catalog
        // owns those ids, so the content compilers check every reference against the compiled
        // catalog. SimulationWorker never loads the item catalog; GameService validates the
        // same ids again when it commits a purchase, a sale, or a gathered yield.
        public static void ValidateItemReferences(
            WorldActorRuntimeDocument runtime,
            ItemCatalogRuntimeDocument items)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            var errors = new List<string>();
            var vendorIds = new HashSet<string>(
                (items.Vendors ?? Array.Empty<VendorProfileDefinition>()).Select(vendor => vendor.Id),
                StringComparer.Ordinal);
            var definitions = (items.Definitions ?? Array.Empty<ItemDefinition>())
                .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
            var toolKindIds = new HashSet<string>(
                (items.ToolKinds ?? Array.Empty<ItemCatalogIdentityEntry>()).Select(kind => kind.Id),
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

            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }
    }
}
