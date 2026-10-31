using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mmorpg.WorldData.Items
{
    // Recompiles a loaded runtime catalog from its own gameplay content, so a reader can
    // trust its revision and fingerprints instead of the file. GameService and Unity both
    // load the compiled catalog through this one conversion.
    public static class ItemCatalogRuntimeVerifier
    {
        public static ItemCatalogRuntimeDocument VerifyAndCanonicalize(
            ItemCatalogRuntimeDocument runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            var canonical = ItemCatalogCompiler.Compile(ToAuthoring(runtime));
            if (!string.Equals(runtime.Revision, canonical.Revision, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The compiled item catalog revision does not match its gameplay content.");
            }

            var runtimeDefinitions = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            foreach (var definition in runtime.Definitions ?? Array.Empty<ItemDefinition>())
            {
                if (definition?.Id != null)
                {
                    runtimeDefinitions[definition.Id] = definition;
                }
            }

            foreach (var definition in canonical.Definitions)
            {
                if (!runtimeDefinitions.TryGetValue(definition.Id, out var runtimeDefinition)
                    || !string.Equals(
                        runtimeDefinition.StructuralFingerprint,
                        definition.StructuralFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Item definition '" + definition.Id + "' has a stale structural fingerprint.");
                }
            }

            var runtimeTiers = new Dictionary<string, SecureContainerTierDefinition>(StringComparer.Ordinal);
            foreach (var tier in runtime.SecureContainerTiers ?? Array.Empty<SecureContainerTierDefinition>())
            {
                if (tier?.Id != null)
                {
                    runtimeTiers[tier.Id] = tier;
                }
            }

            foreach (var tier in canonical.SecureContainerTiers)
            {
                if (!runtimeTiers.TryGetValue(tier.Id, out var runtimeTier)
                    || !string.Equals(
                        runtimeTier.StructuralFingerprint,
                        tier.StructuralFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Secure Container tier '" + tier.Id + "' has a stale structural fingerprint.");
                }
            }

            return canonical;
        }

        // Unity's JsonUtility never yields a null nested object, so a Bag without slots, a
        // weapon without a family, and ammunition without a family, which a valid catalog
        // cannot contain, stand for none.
        public static ItemCatalogAuthoringDocument ToAuthoring(ItemCatalogRuntimeDocument runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            return new ItemCatalogAuthoringDocument
            {
                FormatVersion = runtime.FormatVersion,
                CatalogId = runtime.CatalogId,
                BaseSecureContainerTierId = runtime.BaseSecureContainerTierId,
                Character = runtime.Character == null
                    ? null
                    : new ItemCharacterSettingsEntry
                    {
                        BaseCarryCapacity = runtime.Character.BaseCarryCapacity,
                        InventorySlotCount = runtime.Character.InventorySlotCount,
                        BankSlotCount = runtime.Character.BankSlotCount
                    },
                Economy = runtime.Economy == null
                    ? null
                    : new ItemEconomySettingsEntry
                    {
                        CurrencyDisplayName = runtime.Economy.CurrencyDisplayName,
                        StartingBalance = runtime.Economy.StartingBalance
                    },
                Progression = runtime.Progression == null
                    ? null
                    : new ItemProgressionSettingsEntry
                    {
                        MaximumCharacterLevel = runtime.Progression.MaximumCharacterLevel
                    },
                Categories = runtime.Categories?.Select(CloneIdentity).ToArray(),
                Tags = runtime.Tags?.Select(CloneIdentity).ToArray(),
                ToolKinds = runtime.ToolKinds?.Select(CloneIdentity).ToArray(),
                EquipmentSlots = runtime.EquipmentSlots?
                    .Select(slot => slot == null
                        ? null
                        : new ItemEquipmentSlotEntry
                        {
                            Id = slot.Id,
                            DisplayName = slot.DisplayName,
                            MaximumQuantity = slot.MaximumQuantity
                        })
                    .ToArray(),
                WeaponFamilies = runtime.WeaponFamilies?
                    .Select(family => family == null
                        ? null
                        : new ItemWeaponFamilyEntry
                        {
                            Id = family.Id,
                            DisplayName = family.DisplayName,
                            CombatStyle = family.CombatStyle,
                            AmmunitionFamily = family.AmmunitionFamily,
                            ProjectileSpeed = family.ProjectileSpeed,
                            WindupSeconds = family.WindupSeconds
                        })
                    .ToArray(),
                AmmunitionFamilies = runtime.AmmunitionFamilies?.Select(CloneIdentity).ToArray(),
                Definitions = runtime.Definitions?.Select(ToAuthoringDefinition).ToArray(),
                SecureContainerTiers = runtime.SecureContainerTiers?
                    .Select(tier => tier == null
                        ? null
                        : new SecureContainerTierAuthoringEntry
                        {
                            Id = tier.Id,
                            DisplayName = tier.DisplayName,
                            SlotCapacity = tier.SlotCapacity
                        })
                    .ToArray(),
                Vendors = runtime.Vendors?
                    .Select(vendor => vendor == null
                        ? null
                        : new VendorProfileAuthoringEntry
                        {
                            Id = vendor.Id,
                            DisplayName = vendor.DisplayName,
                            Offers = vendor.Offers?
                                .Select(offer => offer == null
                                    ? null
                                    : new VendorOfferEntry { DefinitionId = offer.DefinitionId })
                                .ToArray(),
                            PurchasePriceBasisPoints = vendor.PurchasePriceBasisPoints,
                            SalePriceBasisPoints = vendor.SalePriceBasisPoints,
                            SaleCategories = vendor.SaleCategories
                        })
                    .ToArray(),
                Retired = runtime.Retired == null
                    ? null
                    : new ItemCatalogRetiredIdsEntry
                    {
                        Categories = runtime.Retired.Categories,
                        Tags = runtime.Retired.Tags,
                        EquipmentSlots = runtime.Retired.EquipmentSlots,
                        Definitions = runtime.Retired.Definitions,
                        SecureContainerTiers = runtime.Retired.SecureContainerTiers
                    }
            };
        }

        private static ItemCatalogIdentityEntry CloneIdentity(ItemCatalogIdentityEntry entry)
        {
            return entry == null
                ? null
                : new ItemCatalogIdentityEntry { Id = entry.Id, DisplayName = entry.DisplayName };
        }

        private static ItemDefinitionAuthoringEntry ToAuthoringDefinition(ItemDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }

            var hasBag = definition.Bag != null
                && definition.Bag.Slots != null
                && definition.Bag.Slots.Length > 0;
            var hasWeapon = definition.Weapon != null
                && !string.IsNullOrEmpty(definition.Weapon.WeaponFamily);
            var hasAmmunition = definition.Ammunition != null
                && !string.IsNullOrEmpty(definition.Ammunition.AmmunitionFamily);
            return new ItemDefinitionAuthoringEntry
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Category = definition.Category,
                Tags = definition.Tags,
                ToolKinds = definition.ToolKinds,
                UnitWeight = definition.UnitWeight,
                Value = definition.Value,
                MaximumStackSize = definition.MaximumStackSize,
                EquipmentSlots = definition.EquipmentSlots,
                BlockedEquipmentSlots = definition.BlockedEquipmentSlots,
                PlayerDestroyable = definition.PlayerDestroyable,
                LocationEligibility = definition.LocationEligibility == null
                    ? null
                    : new ItemLocationEligibilityAuthoringEntry
                    {
                        SecureContainer = definition.LocationEligibility.SecureContainer
                    },
                DefaultPolicies = definition.DefaultPolicies,
                Bag = !hasBag
                    ? null
                    : new BagDefinitionAuthoringEntry
                    {
                        CarryCapacityBonus = definition.Bag.CarryCapacityBonus,
                        Slots = definition.Bag.Slots
                            .Select(slot => slot == null
                                ? null
                                : new BagSlotAuthoringEntry
                                {
                                    Index = slot.Index,
                                    Kind = slot.Kind,
                                    AcceptedTags = slot.AcceptedTags
                                })
                            .ToArray()
                    },
                RequiredCharacterLevel = definition.RequiredCharacterLevel,
                Weapon = !hasWeapon
                    ? null
                    : new ItemWeaponEntry
                    {
                        WeaponFamily = definition.Weapon.WeaponFamily,
                        DamageType = definition.Weapon.DamageType,
                        MinimumDamage = definition.Weapon.MinimumDamage,
                        MaximumDamage = definition.Weapon.MaximumDamage,
                        AttackIntervalSeconds = definition.Weapon.AttackIntervalSeconds,
                        Range = definition.Weapon.Range,
                        ManaCost = definition.Weapon.ManaCost
                    },
                Ammunition = !hasAmmunition
                    ? null
                    : new ItemAmmunitionEntry
                    {
                        AmmunitionFamily = definition.Ammunition.AmmunitionFamily,
                        DamageBonusPercent = definition.Ammunition.DamageBonusPercent
                    }
            };
        }
    }
}
