using System;
using System.Linq;

namespace Mmorpg.WorldData.Items
{
    // The camelCase field shape of item catalog JSON for Unity's JsonUtility. The Item Catalog
    // editor reads and writes authoring files with it, and the Unity client reads compiled
    // runtime catalogs with it, so a new catalog field is added here once.
    [Serializable]
    public sealed class ItemCatalogJsonIdentity
    {
        public string id;
        public string displayName;
    }

    [Serializable]
    public sealed class ItemCatalogJsonEquipmentSlot
    {
        public string id;
        public string displayName;
        public int maximumQuantity;
    }

    [Serializable]
    public sealed class ItemCatalogJsonCharacter
    {
        public long baseCarryCapacity;
        public int inventorySlotCount;
        public int bankSlotCount;
    }

    // Released ids that left the catalog. They stay locked so they are never reused.
    [Serializable]
    public sealed class ItemCatalogJsonRetired
    {
        public string[] categories;
        public string[] tags;
        public string[] equipmentSlots;
        public string[] definitions;
        public string[] secureContainerTiers;
    }

    [Serializable]
    public sealed class ItemCatalogJsonLocationEligibility
    {
        public bool secureContainer;
    }

    [Serializable]
    public sealed class ItemCatalogJsonBagSlot
    {
        public int index;
        public string kind;
        public string[] acceptedTags;
    }

    [Serializable]
    public sealed class ItemCatalogJsonBagDefinition
    {
        public long carryCapacityBonus;
        public ItemCatalogJsonBagSlot[] slots;
    }

    [Serializable]
    public sealed class ItemCatalogJsonDefinition
    {
        public string id;
        public string displayName;
        public string category;
        public string[] tags;
        public long unitWeight;
        public int maximumStackSize;
        public string[] equipmentSlots;
        public string[] blockedEquipmentSlots;
        public bool playerDestroyable;
        public ItemCatalogJsonLocationEligibility locationEligibility;
        public string[] defaultPolicies;
        public ItemCatalogJsonBagDefinition bag;

        // JsonUtility materializes an empty nested class for a missing Bag field, so a
        // non-Bag definition with a slotless Bag has no Bag.
        public void ClearMissingBagPlaceholder()
        {
            if (bag != null
                && (bag.slots == null || bag.slots.Length == 0)
                && !string.Equals(category, ItemCategoryIds.Bag, StringComparison.Ordinal))
            {
                bag = null;
            }
        }

        public ItemDefinitionAuthoringEntry ToAuthoringEntry()
        {
            return new ItemDefinitionAuthoringEntry
            {
                Id = id,
                DisplayName = displayName,
                Category = category,
                Tags = tags ?? Array.Empty<string>(),
                UnitWeight = unitWeight,
                MaximumStackSize = maximumStackSize,
                EquipmentSlots = equipmentSlots ?? Array.Empty<string>(),
                BlockedEquipmentSlots = blockedEquipmentSlots ?? Array.Empty<string>(),
                PlayerDestroyable = playerDestroyable,
                LocationEligibility = locationEligibility == null
                    ? null
                    : new ItemLocationEligibilityAuthoringEntry
                    {
                        SecureContainer = locationEligibility.secureContainer
                    },
                DefaultPolicies = defaultPolicies ?? Array.Empty<string>(),
                Bag = bag == null
                    ? null
                    : new BagDefinitionAuthoringEntry
                    {
                        CarryCapacityBonus = bag.carryCapacityBonus,
                        Slots = (bag.slots ?? Array.Empty<ItemCatalogJsonBagSlot>())
                            .Select(slot => new BagSlotAuthoringEntry
                            {
                                Index = slot.index,
                                Kind = slot.kind,
                                AcceptedTags = slot.acceptedTags ?? Array.Empty<string>()
                            })
                            .ToArray()
                    }
            };
        }
    }

    [Serializable]
    public sealed class ItemCatalogJsonSecureContainerTier
    {
        public string id;
        public string displayName;
        public int slotCapacity;
    }

    // A complete catalog: catalog.json together with every definition, or a compiled
    // runtime catalog without its revision and fingerprints.
    [Serializable]
    public sealed class ItemCatalogJsonDocument
    {
        public int formatVersion;
        public string catalogId;
        public string baseSecureContainerTierId;
        public ItemCatalogJsonCharacter character;
        public ItemCatalogJsonIdentity[] categories;
        public ItemCatalogJsonIdentity[] tags;
        public ItemCatalogJsonEquipmentSlot[] equipmentSlots;
        public ItemCatalogJsonDefinition[] definitions;
        public ItemCatalogJsonSecureContainerTier[] secureContainerTiers;
        public ItemCatalogJsonRetired retired;

        public ItemCatalogAuthoringDocument ToAuthoringDocument()
        {
            return new ItemCatalogAuthoringDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                BaseSecureContainerTierId = baseSecureContainerTierId,
                Character = character == null
                    ? null
                    : new ItemCharacterSettingsEntry
                    {
                        BaseCarryCapacity = character.baseCarryCapacity,
                        InventorySlotCount = character.inventorySlotCount,
                        BankSlotCount = character.bankSlotCount
                    },
                Categories = ConvertIdentities(categories),
                Tags = ConvertIdentities(tags),
                EquipmentSlots = (equipmentSlots ?? Array.Empty<ItemCatalogJsonEquipmentSlot>())
                    .Select(slot => new ItemEquipmentSlotEntry
                    {
                        Id = slot.id,
                        DisplayName = slot.displayName,
                        MaximumQuantity = slot.maximumQuantity
                    })
                    .ToArray(),
                Definitions = (definitions ?? Array.Empty<ItemCatalogJsonDefinition>())
                    .Select(definition => definition.ToAuthoringEntry())
                    .ToArray(),
                SecureContainerTiers = (secureContainerTiers
                        ?? Array.Empty<ItemCatalogJsonSecureContainerTier>())
                    .Select(tier => new SecureContainerTierAuthoringEntry
                    {
                        Id = tier.id,
                        DisplayName = tier.displayName,
                        SlotCapacity = tier.slotCapacity
                    })
                    .ToArray(),
                Retired = retired == null
                    ? null
                    : new ItemCatalogRetiredIdsEntry
                    {
                        Categories = retired.categories ?? Array.Empty<string>(),
                        Tags = retired.tags ?? Array.Empty<string>(),
                        EquipmentSlots = retired.equipmentSlots ?? Array.Empty<string>(),
                        Definitions = retired.definitions ?? Array.Empty<string>(),
                        SecureContainerTiers = retired.secureContainerTiers ?? Array.Empty<string>()
                    }
            };
        }

        private static ItemCatalogIdentityEntry[] ConvertIdentities(
            ItemCatalogJsonIdentity[] identities)
        {
            return (identities ?? Array.Empty<ItemCatalogJsonIdentity>())
                .Select(identity => new ItemCatalogIdentityEntry
                {
                    Id = identity.id,
                    DisplayName = identity.displayName
                })
                .ToArray();
        }
    }
}
