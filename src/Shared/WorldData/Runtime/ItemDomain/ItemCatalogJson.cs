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

    [Serializable]
    public sealed class ItemCatalogJsonEconomy
    {
        public string currencyDisplayName;
        public long startingBalance;
    }

    [Serializable]
    public sealed class ItemCatalogJsonProgression
    {
        public int maximumCharacterLevel;
    }

    [Serializable]
    public sealed class ItemCatalogJsonWeaponFamily
    {
        public string id;
        public string displayName;
        public string combatStyle;
        public string ammunitionFamily;
        public float projectileSpeed;
        public float windupSeconds = -1f;
    }

    [Serializable]
    public sealed class ItemCatalogJsonWeapon
    {
        public string weaponFamily;
        public string damageType;
        public int minimumDamage;
        public int maximumDamage;
        public float attackIntervalSeconds;
        public float range;
        public int manaCost;
    }

    [Serializable]
    public sealed class ItemCatalogJsonAmmunition
    {
        public string ammunitionFamily;
        public int damageBonusPercent;
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
        public string[] toolKinds;
        public long unitWeight;
        public long value;
        public int maximumStackSize;
        public string[] equipmentSlots;
        public string[] blockedEquipmentSlots;
        public bool playerDestroyable;
        public ItemCatalogJsonLocationEligibility locationEligibility;
        public string[] defaultPolicies;
        public ItemCatalogJsonBagDefinition bag;
        public int requiredCharacterLevel;
        public ItemCatalogJsonWeapon weapon;
        public ItemCatalogJsonAmmunition ammunition;

        // JsonUtility materializes an empty nested class for a missing nested field, so a
        // non-Bag definition with a slotless Bag has no Bag, and a weapon or ammunition
        // block without a family is no block, unless the category requires one.
        public void ClearMissingPlaceholders()
        {
            if (bag != null
                && (bag.slots == null || bag.slots.Length == 0)
                && !string.Equals(category, ItemCategoryIds.Bag, StringComparison.Ordinal))
            {
                bag = null;
            }

            if (weapon != null
                && string.IsNullOrEmpty(weapon.weaponFamily)
                && !string.Equals(category, ItemCategoryIds.Weapon, StringComparison.Ordinal))
            {
                weapon = null;
            }

            if (ammunition != null
                && string.IsNullOrEmpty(ammunition.ammunitionFamily)
                && !string.Equals(category, ItemCategoryIds.Ammunition, StringComparison.Ordinal))
            {
                ammunition = null;
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
                ToolKinds = toolKinds ?? Array.Empty<string>(),
                UnitWeight = unitWeight,
                Value = value,
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
                    },
                RequiredCharacterLevel = requiredCharacterLevel,
                Weapon = weapon == null
                    ? null
                    : new ItemWeaponEntry
                    {
                        WeaponFamily = weapon.weaponFamily,
                        DamageType = weapon.damageType,
                        MinimumDamage = weapon.minimumDamage,
                        MaximumDamage = weapon.maximumDamage,
                        AttackIntervalSeconds = weapon.attackIntervalSeconds,
                        Range = weapon.range,
                        ManaCost = weapon.manaCost
                    },
                Ammunition = ammunition == null
                    ? null
                    : new ItemAmmunitionEntry
                    {
                        AmmunitionFamily = ammunition.ammunitionFamily,
                        DamageBonusPercent = ammunition.damageBonusPercent
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

    [Serializable]
    public sealed class ItemCatalogJsonVendorOffer
    {
        public string definitionId;
    }

    [Serializable]
    public sealed class ItemCatalogJsonVendor
    {
        public string id;
        public string displayName;
        public ItemCatalogJsonVendorOffer[] offers;
        public int purchasePriceBasisPoints;
        public int salePriceBasisPoints;
        public string[] saleCategories;

        public VendorProfileAuthoringEntry ToAuthoringEntry()
        {
            return new VendorProfileAuthoringEntry
            {
                Id = id,
                DisplayName = displayName,
                Offers = (offers ?? Array.Empty<ItemCatalogJsonVendorOffer>())
                    .Select(offer => new VendorOfferEntry { DefinitionId = offer.definitionId })
                    .ToArray(),
                PurchasePriceBasisPoints = purchasePriceBasisPoints,
                SalePriceBasisPoints = salePriceBasisPoints,
                SaleCategories = saleCategories ?? Array.Empty<string>()
            };
        }
    }

    // A complete catalog: catalog.json together with every definition and vendor, or a
    // compiled runtime catalog without its revision and fingerprints.
    [Serializable]
    public sealed class ItemCatalogJsonDocument
    {
        public int formatVersion;
        public string catalogId;
        public string baseSecureContainerTierId;
        public ItemCatalogJsonCharacter character;
        public ItemCatalogJsonEconomy economy;
        public ItemCatalogJsonProgression progression;
        public ItemCatalogJsonIdentity[] categories;
        public ItemCatalogJsonIdentity[] tags;
        public ItemCatalogJsonIdentity[] toolKinds;
        public ItemCatalogJsonEquipmentSlot[] equipmentSlots;
        public ItemCatalogJsonWeaponFamily[] weaponFamilies;
        public ItemCatalogJsonIdentity[] ammunitionFamilies;
        public ItemCatalogJsonDefinition[] definitions;
        public ItemCatalogJsonSecureContainerTier[] secureContainerTiers;
        public ItemCatalogJsonVendor[] vendors;
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
                Economy = economy == null
                    ? null
                    : new ItemEconomySettingsEntry
                    {
                        CurrencyDisplayName = economy.currencyDisplayName,
                        StartingBalance = economy.startingBalance
                    },
                Progression = progression == null
                    ? null
                    : new ItemProgressionSettingsEntry
                    {
                        MaximumCharacterLevel = progression.maximumCharacterLevel
                    },
                Categories = ConvertIdentities(categories),
                Tags = ConvertIdentities(tags),
                ToolKinds = ConvertIdentities(toolKinds),
                EquipmentSlots = (equipmentSlots ?? Array.Empty<ItemCatalogJsonEquipmentSlot>())
                    .Select(slot => new ItemEquipmentSlotEntry
                    {
                        Id = slot.id,
                        DisplayName = slot.displayName,
                        MaximumQuantity = slot.maximumQuantity
                    })
                    .ToArray(),
                WeaponFamilies = (weaponFamilies ?? Array.Empty<ItemCatalogJsonWeaponFamily>())
                    .Select(family => new ItemWeaponFamilyEntry
                    {
                        Id = family.id,
                        DisplayName = family.displayName,
                        CombatStyle = family.combatStyle,
                        AmmunitionFamily = family.ammunitionFamily ?? string.Empty,
                        ProjectileSpeed = family.projectileSpeed,
                        WindupSeconds = family.windupSeconds
                    })
                    .ToArray(),
                AmmunitionFamilies = ConvertIdentities(ammunitionFamilies),
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
                Vendors = (vendors ?? Array.Empty<ItemCatalogJsonVendor>())
                    .Select(vendor => vendor.ToAuthoringEntry())
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
