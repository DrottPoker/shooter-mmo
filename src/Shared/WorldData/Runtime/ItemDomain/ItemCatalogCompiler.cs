using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mmorpg.WorldData.Combat;

namespace Mmorpg.WorldData.Items
{
    public sealed class ItemCatalogValidationException : Exception
    {
        public ItemCatalogValidationException(IReadOnlyCollection<string> errors)
            : base(CreateMessage(errors))
        {
            Errors = errors.ToArray();
        }

        public IReadOnlyList<string> Errors { get; }

        private static string CreateMessage(IReadOnlyCollection<string> errors)
        {
            return "Item catalog validation failed:"
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    errors.Select(error => "- " + error));
        }
    }

    public static class ItemCatalogCompiler
    {
        private const int MaximumIdentifierLength = ItemCatalogFormat.MaximumIdentifierLength;
        private const int MaximumDisplayNameLength = 128;
        private const int MaximumContainerSlotCapacity = ItemCatalogFormat.MaximumContainerSlotCapacity;
        private const long MaximumCarryCapacity = 1_000_000_000_000L;

        // Only the ids that rules give a meaning to are required. Every other id is content.
        private static readonly string[] RequiredCategories =
        {
            ItemCategoryIds.Weapon,
            ItemCategoryIds.Ammunition,
            ItemCategoryIds.Bag,
            ItemCategoryIds.QuestItem
        };

        private static readonly string[] RequiredEquipmentSlots =
        {
            ItemEquipmentSlotIds.Bag
        };

        private static readonly HashSet<string> KnownDefaultPolicies =
            new HashSet<string>(StringComparer.Ordinal)
            {
                ItemPolicyIds.ProtectedOnDeath,
                ItemPolicyIds.Insured
            };

        public static ItemCatalogRuntimeDocument Compile(
            ItemCatalogAuthoringDocument authoring)
        {
            if (authoring == null)
            {
                throw new ArgumentNullException(nameof(authoring));
            }

            Validate(authoring);

            var catalog = new ItemCatalogRuntimeDocument
            {
                FormatVersion = ItemCatalogFormat.Version,
                CatalogId = authoring.CatalogId,
                Revision = string.Empty,
                BaseSecureContainerTierId = authoring.BaseSecureContainerTierId,
                Character = new ItemCharacterSettingsEntry
                {
                    BaseCarryCapacity = authoring.Character.BaseCarryCapacity,
                    InventorySlotCount = authoring.Character.InventorySlotCount,
                    BankSlotCount = authoring.Character.BankSlotCount
                },
                Economy = new ItemEconomySettingsEntry
                {
                    CurrencyDisplayName = authoring.Economy.CurrencyDisplayName,
                    StartingBalance = authoring.Economy.StartingBalance
                },
                Progression = new ItemProgressionSettingsEntry
                {
                    MaximumCharacterLevel = authoring.Progression.MaximumCharacterLevel
                },
                Categories = CompileIdentities(authoring.Categories),
                Tags = CompileIdentities(authoring.Tags),
                ToolKinds = CompileIdentities(authoring.ToolKinds),
                EquipmentSlots = authoring.EquipmentSlots
                    .Select(slot => new ItemEquipmentSlotEntry
                    {
                        Id = slot.Id,
                        DisplayName = slot.DisplayName,
                        MaximumQuantity = slot.MaximumQuantity
                    })
                    .ToArray(),
                WeaponFamilies = authoring.WeaponFamilies
                    .Select(family => new ItemWeaponFamilyEntry
                    {
                        Id = family.Id,
                        DisplayName = family.DisplayName,
                        CombatStyle = family.CombatStyle,
                        AmmunitionFamily = family.AmmunitionFamily,
                        ProjectileSpeed = family.ProjectileSpeed
                    })
                    .OrderBy(family => family.Id, StringComparer.Ordinal)
                    .ToArray(),
                AmmunitionFamilies = CompileIdentities(authoring.AmmunitionFamilies),
                Definitions = authoring.Definitions
                    .Select(CompileDefinition)
                    .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                    .ToArray(),
                SecureContainerTiers = authoring.SecureContainerTiers
                    .Select(CompileSecureContainerTier)
                    .OrderBy(tier => tier.Id, StringComparer.Ordinal)
                    .ToArray(),
                Vendors = authoring.Vendors
                    .Select(CompileVendor)
                    .OrderBy(vendor => vendor.Id, StringComparer.Ordinal)
                    .ToArray(),
                Retired = new ItemCatalogRetiredIdsEntry
                {
                    Categories = SortStrings(authoring.Retired.Categories),
                    Tags = SortStrings(authoring.Retired.Tags),
                    EquipmentSlots = SortStrings(authoring.Retired.EquipmentSlots),
                    Definitions = SortStrings(authoring.Retired.Definitions),
                    SecureContainerTiers = SortStrings(authoring.Retired.SecureContainerTiers)
                }
            };
            catalog.Revision = ComputeCatalogRevision(catalog);
            return catalog;
        }

        private static void Validate(ItemCatalogAuthoringDocument authoring)
        {
            var errors = new List<string>();
            if (authoring.FormatVersion != ItemCatalogFormat.Version)
            {
                errors.Add("formatVersion is unsupported.");
            }

            ValidateIdentifier(authoring.CatalogId, "catalogId", errors);
            ValidateIdentifier(
                authoring.BaseSecureContainerTierId,
                "baseSecureContainerTierId",
                errors);
            ValidateCharacterSettings(authoring.Character, errors);
            ValidateEconomySettings(authoring.Economy, errors);
            var maximumCharacterLevel = ValidateProgression(authoring.Progression, errors);

            var categoryIds = ValidateIdentities(
                authoring.Categories,
                "categories",
                MaximumIdentifierLength,
                errors);
            var tagIds = ValidateIdentities(
                authoring.Tags,
                "tags",
                ItemCatalogFormat.MaximumTagLength,
                errors);
            var toolKindIds = ValidateIdentities(
                authoring.ToolKinds,
                "toolKinds",
                MaximumIdentifierLength,
                errors,
                true);
            var equipmentSlots = ValidateEquipmentSlots(authoring.EquipmentSlots, errors);
            var ammunitionFamilyIds = ValidateIdentities(
                authoring.AmmunitionFamilies,
                "ammunitionFamilies",
                MaximumIdentifierLength,
                errors,
                allowEmpty: true);
            var weaponFamilyIds = ValidateWeaponFamilies(
                authoring.WeaponFamilies,
                ammunitionFamilyIds,
                errors);

            ValidateRequiredIds(categoryIds, RequiredCategories, "categories", errors);
            ValidateRequiredIds(
                new HashSet<string>(equipmentSlots.Keys, StringComparer.Ordinal),
                RequiredEquipmentSlots,
                "equipmentSlots",
                errors);

            var definitionIds = ValidateDefinitions(
                authoring.Definitions,
                categoryIds,
                tagIds,
                toolKindIds,
                equipmentSlots,
                new CombatReferences(
                    weaponFamilyIds,
                    MagicWeaponFamilyIds(authoring.WeaponFamilies),
                    ammunitionFamilyIds,
                    maximumCharacterLevel),
                errors);
            ValidateVendors(authoring.Vendors, authoring.Definitions, categoryIds, errors);
            var tierIds = ValidateSecureContainerTiers(
                authoring.SecureContainerTiers,
                authoring.BaseSecureContainerTierId,
                errors);
            ValidateRetiredIds(
                authoring.Retired,
                categoryIds,
                tagIds,
                new HashSet<string>(equipmentSlots.Keys, StringComparer.Ordinal),
                definitionIds,
                tierIds,
                errors);

            if (errors.Count > 0)
            {
                throw new ItemCatalogValidationException(errors);
            }
        }

        private static void ValidateCharacterSettings(
            ItemCharacterSettingsEntry character,
            ICollection<string> errors)
        {
            if (character == null)
            {
                errors.Add("character is required.");
                return;
            }

            if (character.BaseCarryCapacity <= 0 || character.BaseCarryCapacity > MaximumCarryCapacity)
            {
                errors.Add(
                    "character.baseCarryCapacity must be between 1 and "
                    + MaximumCarryCapacity.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            ValidateSlotCount(character.InventorySlotCount, "character.inventorySlotCount", errors);
            ValidateSlotCount(character.BankSlotCount, "character.bankSlotCount", errors);
        }

        private static void ValidateEconomySettings(
            ItemEconomySettingsEntry economy,
            ICollection<string> errors)
        {
            if (economy == null)
            {
                errors.Add("economy is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(economy.CurrencyDisplayName)
                || economy.CurrencyDisplayName.Length > ItemCatalogFormat.MaximumCurrencyDisplayNameLength
                || !string.Equals(
                    economy.CurrencyDisplayName,
                    economy.CurrencyDisplayName.Trim(),
                    StringComparison.Ordinal))
            {
                errors.Add(
                    "economy.currencyDisplayName is required, must be trimmed, and must not exceed "
                    + ItemCatalogFormat.MaximumCurrencyDisplayNameLength
                    + " characters.");
            }

            if (economy.StartingBalance < 0
                || economy.StartingBalance > VendorPricing.MaximumCurrencyBalance)
            {
                errors.Add(
                    "economy.startingBalance is required and must be between 0 and "
                    + VendorPricing.MaximumCurrencyBalance.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }
        }

        private static int ValidateProgression(
            ItemProgressionSettingsEntry progression,
            ICollection<string> errors)
        {
            if (progression == null)
            {
                errors.Add("progression is required.");
                return 0;
            }

            if (progression.MaximumCharacterLevel < 1
                || progression.MaximumCharacterLevel > CombatContentRules.MaximumCharacterLevel)
            {
                errors.Add(
                    "progression.maximumCharacterLevel must be between 1 and "
                    + CombatContentRules.MaximumCharacterLevel.ToString(CultureInfo.InvariantCulture)
                    + ".");
                return 0;
            }

            return progression.MaximumCharacterLevel;
        }

        private static HashSet<string> ValidateWeaponFamilies(
            ItemWeaponFamilyEntry[] families,
            ISet<string> ammunitionFamilyIds,
            ICollection<string> errors)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (families == null)
            {
                errors.Add("weaponFamilies is required.");
                return ids;
            }

            for (var index = 0; index < families.Length; index++)
            {
                var family = families[index];
                var path = "weaponFamilies[" + index + "]";
                if (family == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(family.Id, path + ".id", errors);
                ValidateDisplayName(family.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(family.Id) && !ids.Add(family.Id))
                {
                    errors.Add("weaponFamilies contains duplicate id '" + family.Id + "'.");
                }

                if (!CombatStyleIds.IsKnown(family.CombatStyle))
                {
                    errors.Add(path + ".combatStyle must be melee, ranged, or magic.");
                    continue;
                }

                if (family.AmmunitionFamily == null)
                {
                    errors.Add(path + ".ammunitionFamily is required.");
                }
                else if (string.Equals(family.CombatStyle, CombatStyleIds.Ranged, StringComparison.Ordinal))
                {
                    ValidateIdentifier(family.AmmunitionFamily, path + ".ammunitionFamily", errors);
                    ValidateReference(
                        family.AmmunitionFamily,
                        ammunitionFamilyIds,
                        path + ".ammunitionFamily",
                        "ammunition family",
                        errors);
                }
                else if (family.AmmunitionFamily.Length != 0)
                {
                    errors.Add(path + ".ammunitionFamily must be empty unless the style is ranged.");
                }

                ValidateProjectileSpeed(family, path, errors);
            }

            return ids;
        }

        // Ranged projectiles fly, Melee hits at once, and Magic may do either.
        private static void ValidateProjectileSpeed(
            ItemWeaponFamilyEntry family,
            string path,
            ICollection<string> errors)
        {
            var speed = family.ProjectileSpeed;
            var maximum = CombatContentRules.MaximumProjectileSpeedMetresPerSecond;
            if (!CombatContentRules.IsFinite(speed) || speed < 0f || speed > maximum)
            {
                errors.Add(
                    path + ".projectileSpeed must be between 0 and "
                    + maximum.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else if (string.Equals(family.CombatStyle, CombatStyleIds.Ranged, StringComparison.Ordinal)
                && speed <= 0f)
            {
                errors.Add(path + ".projectileSpeed must be positive for a ranged family.");
            }
            else if (string.Equals(family.CombatStyle, CombatStyleIds.Melee, StringComparison.Ordinal)
                && speed != 0f)
            {
                errors.Add(path + ".projectileSpeed must be 0 for a melee family.");
            }
        }

        private static void ValidateSlotCount(int value, string path, ICollection<string> errors)
        {
            if (value <= 0 || value > MaximumContainerSlotCapacity)
            {
                errors.Add(path + " must be between 1 and " + MaximumContainerSlotCapacity + ".");
            }
        }

        private static HashSet<string> ValidateIdentities(
            ItemCatalogIdentityEntry[] entries,
            string collectionName,
            int maximumLength,
            ICollection<string> errors,
            bool allowEmpty = false)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (entries == null)
            {
                errors.Add(collectionName + (allowEmpty
                    ? " is required."
                    : " must contain at least one entry."));
                return ids;
            }

            if (entries.Length == 0 && !allowEmpty)
            {
                errors.Add(collectionName + " must contain at least one entry.");
                return ids;
            }

            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                var path = collectionName + "[" + index + "]";
                if (entry == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(entry.Id, path + ".id", errors, maximumLength);
                ValidateDisplayName(entry.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(entry.Id) && !ids.Add(entry.Id))
                {
                    errors.Add(collectionName + " contains duplicate id '" + entry.Id + "'.");
                }
            }

            return ids;
        }

        private static Dictionary<string, ItemEquipmentSlotEntry> ValidateEquipmentSlots(
            ItemEquipmentSlotEntry[] slots,
            ICollection<string> errors)
        {
            var byId = new Dictionary<string, ItemEquipmentSlotEntry>(StringComparer.Ordinal);
            if (slots == null || slots.Length == 0)
            {
                errors.Add("equipmentSlots must contain at least one entry.");
                return byId;
            }

            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                var path = "equipmentSlots[" + index + "]";
                if (slot == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(
                    slot.Id,
                    path + ".id",
                    errors,
                    ItemCatalogFormat.MaximumEquipmentSlotIdLength);
                ValidateDisplayName(slot.DisplayName, path + ".displayName", errors);
                if (slot.MaximumQuantity <= 0 || slot.MaximumQuantity > ItemCatalogFormat.MaximumQuantity)
                {
                    errors.Add(
                        path
                        + ".maximumQuantity must be between 1 and "
                        + ItemCatalogFormat.MaximumQuantity.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }

                if (string.Equals(slot.Id, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal)
                    && slot.MaximumQuantity != 1)
                {
                    errors.Add(path + ".maximumQuantity must be 1 for the Bag equipment slot.");
                }

                if (!string.IsNullOrWhiteSpace(slot.Id))
                {
                    if (byId.ContainsKey(slot.Id))
                    {
                        errors.Add("equipmentSlots contains duplicate id '" + slot.Id + "'.");
                    }
                    else
                    {
                        byId.Add(slot.Id, slot);
                    }
                }
            }

            return byId;
        }

        private static void ValidateRequiredIds(
            ISet<string> actualIds,
            IEnumerable<string> requiredIds,
            string collectionName,
            ICollection<string> errors)
        {
            foreach (var requiredId in requiredIds)
            {
                if (!actualIds.Contains(requiredId))
                {
                    errors.Add(collectionName + " is missing required id '" + requiredId + "'.");
                }
            }
        }

        private static HashSet<string> ValidateDefinitions(
            ItemDefinitionAuthoringEntry[] definitions,
            ISet<string> categoryIds,
            ISet<string> tagIds,
            ISet<string> toolKindIds,
            IReadOnlyDictionary<string, ItemEquipmentSlotEntry> equipmentSlots,
            CombatReferences combat,
            ICollection<string> errors)
        {
            var definitionIds = new HashSet<string>(StringComparer.Ordinal);
            if (definitions == null || definitions.Length == 0)
            {
                errors.Add("definitions must contain at least one entry.");
                return definitionIds;
            }

            var equipmentSlotIds = new HashSet<string>(equipmentSlots.Keys, StringComparer.Ordinal);
            for (var index = 0; index < definitions.Length; index++)
            {
                var definition = definitions[index];
                var path = "definitions[" + index + "]";
                if (definition == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(definition.Id, path + ".id", errors);
                ValidateDisplayName(definition.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(definition.Id)
                    && !definitionIds.Add(definition.Id))
                {
                    errors.Add("definitions contains duplicate id '" + definition.Id + "'.");
                }

                ValidateIdentifier(definition.Category, path + ".category", errors);
                ValidateReference(
                    definition.Category,
                    categoryIds,
                    path + ".category",
                    "category",
                    errors);
                ValidateReferenceList(
                    definition.Tags,
                    tagIds,
                    path + ".tags",
                    "tag",
                    errors);
                ValidateReferenceList(
                    definition.EquipmentSlots,
                    equipmentSlotIds,
                    path + ".equipmentSlots",
                    "equipment slot",
                    errors);
                ValidateReferenceList(
                    definition.BlockedEquipmentSlots,
                    equipmentSlotIds,
                    path + ".blockedEquipmentSlots",
                    "equipment slot",
                    errors);
                ValidateReferenceList(
                    definition.ToolKinds,
                    toolKindIds,
                    path + ".toolKinds",
                    "tool kind",
                    errors);
                ValidateDefaultPolicies(definition.DefaultPolicies, path, errors);

                if (definition.UnitWeight < 0)
                {
                    errors.Add(path + ".unitWeight must not be negative.");
                }

                if (definition.Value < 0 || definition.Value > ItemCatalogFormat.MaximumItemValue)
                {
                    errors.Add(
                        path
                        + ".value is required and must be between 0 and "
                        + ItemCatalogFormat.MaximumItemValue.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }

                if (definition.ToolKinds != null && definition.ToolKinds.Length > 0)
                {
                    if (definition.ToolKinds.Length > ItemCatalogFormat.MaximumToolKindsPerDefinition)
                    {
                        errors.Add(
                            path + ".toolKinds must not contain more than "
                            + ItemCatalogFormat.MaximumToolKindsPerDefinition
                            + " tool kinds.");
                    }

                    if (!Contains(definition.EquipmentSlots, ItemEquipmentSlotIds.Tool))
                    {
                        errors.Add(
                            path + ".toolKinds requires compatibility with the '"
                            + ItemEquipmentSlotIds.Tool
                            + "' equipment slot.");
                    }
                }

                if (definition.MaximumStackSize <= 0
                    || definition.MaximumStackSize > ItemCatalogFormat.MaximumQuantity)
                {
                    errors.Add(
                        path
                        + ".maximumStackSize must be between 1 and "
                        + ItemCatalogFormat.MaximumQuantity.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }

                if (definition.LocationEligibility == null)
                {
                    errors.Add(path + ".locationEligibility is required.");
                }
                else if (!definition.LocationEligibility.SecureContainer.HasValue)
                {
                    errors.Add(path + ".locationEligibility.secureContainer is required.");
                }
                else if (string.Equals(
                        definition.Category,
                        ItemCategoryIds.Weapon,
                        StringComparison.Ordinal)
                    && definition.LocationEligibility.SecureContainer.Value)
                {
                    errors.Add(path + " cannot allow a weapon in Secure Container.");
                }

                if (!definition.PlayerDestroyable.HasValue)
                {
                    errors.Add(path + ".playerDestroyable is required.");
                }

                ValidateEquipmentStacking(definition, equipmentSlots, path, errors);
                ValidateBlockedEquipmentSlots(definition, path, errors);
                ValidateBagDefinition(definition, path, tagIds, errors);
                ValidateRequiredCharacterLevel(definition, combat, path, errors);
                ValidateWeapon(definition, combat, path, errors);
                ValidateAmmunition(definition, combat, path, errors);

                if (string.Equals(
                        definition.Category,
                        ItemCategoryIds.QuestItem,
                        StringComparison.Ordinal)
                    && Contains(
                        definition.DefaultPolicies,
                        ItemPolicyIds.ProtectedOnDeath)
                    && definition.PlayerDestroyable.GetValueOrDefault())
                {
                    errors.Add(path + " protected quest items cannot be player-destroyable.");
                }

                var hasEquipmentCompatibility = definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Length > 0;
                if (Contains(definition.DefaultPolicies, ItemPolicyIds.Insured)
                    && (!hasEquipmentCompatibility || definition.MaximumStackSize != 1))
                {
                    errors.Add(path + " insured defaults require non-stackable equipment.");
                }
            }

            return definitionIds;
        }

        private static void ValidateRequiredCharacterLevel(
            ItemDefinitionAuthoringEntry definition,
            CombatReferences combat,
            string path,
            ICollection<string> errors)
        {
            if (definition.RequiredCharacterLevel < 1
                || (combat.MaximumCharacterLevel > 0
                    && definition.RequiredCharacterLevel > combat.MaximumCharacterLevel))
            {
                errors.Add(
                    path + ".requiredCharacterLevel must be between 1 and "
                    + "progression.maximumCharacterLevel.");
            }
        }

        // A weapon definition carries exactly one weapon block, and only a weapon does.
        private static void ValidateWeapon(
            ItemDefinitionAuthoringEntry definition,
            CombatReferences combat,
            string path,
            ICollection<string> errors)
        {
            var isWeapon = string.Equals(
                definition.Category,
                ItemCategoryIds.Weapon,
                StringComparison.Ordinal);
            var weapon = definition.Weapon;
            if (weapon == null)
            {
                if (isWeapon)
                {
                    errors.Add(path + " uses category 'weapon' but has no weapon block.");
                }

                return;
            }

            if (!isWeapon)
            {
                errors.Add(path + " has a weapon block but does not use category 'weapon'.");
            }

            if (definition.EquipmentSlots == null || definition.EquipmentSlots.Length == 0)
            {
                errors.Add(path + ".weapon requires equipment compatibility.");
            }

            ValidateIdentifier(weapon.WeaponFamily, path + ".weapon.weaponFamily", errors);
            ValidateReference(
                weapon.WeaponFamily,
                combat.WeaponFamilyIds,
                path + ".weapon.weaponFamily",
                "weapon family",
                errors);
            if (!DamageTypeIds.IsKnown(weapon.DamageType))
            {
                errors.Add(path + ".weapon.damageType must be normal, fire, cold, nature, or shadow.");
            }

            if (!CombatContentRules.IsValidDamageRange(weapon.MinimumDamage, weapon.MaximumDamage)
                || weapon.MaximumDamage < 1)
            {
                errors.Add(
                    path + ".weapon.minimumDamage and maximumDamage must satisfy "
                    + "0 <= minimum <= maximum, with a maximum between 1 and "
                    + CombatContentRules.MaximumDamage.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            if (!CombatContentRules.IsValidAttackInterval(weapon.AttackIntervalSeconds))
            {
                errors.Add(
                    path + ".weapon.attackIntervalSeconds must be between "
                    + CombatContentRules.MinimumAttackIntervalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " and "
                    + CombatContentRules.MaximumAttackIntervalSeconds.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            if (!CombatContentRules.IsValidAttackRange(weapon.Range))
            {
                errors.Add(
                    path + ".weapon.range must be above 0 and at most "
                    + CombatContentRules.MaximumAttackRangeMetres.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            // Magic attacks cost mana and no other attack does.
            var magic = combat.MagicWeaponFamilyIds.Contains(weapon.WeaponFamily ?? string.Empty);
            if (magic && (weapon.ManaCost < 1 || weapon.ManaCost > CombatContentRules.MaximumMana))
            {
                errors.Add(
                    path + ".weapon.manaCost must be between 1 and "
                    + CombatContentRules.MaximumMana.ToString(CultureInfo.InvariantCulture)
                    + " for a magic weapon.");
            }
            else if (!magic && weapon.ManaCost != 0)
            {
                errors.Add(path + ".weapon.manaCost must be 0 unless the weapon family is magic.");
            }
        }

        // An ammunition definition carries exactly one ammunition block, and only it does.
        private static void ValidateAmmunition(
            ItemDefinitionAuthoringEntry definition,
            CombatReferences combat,
            string path,
            ICollection<string> errors)
        {
            var isAmmunition = string.Equals(
                definition.Category,
                ItemCategoryIds.Ammunition,
                StringComparison.Ordinal);
            var ammunition = definition.Ammunition;
            if (ammunition == null)
            {
                if (isAmmunition)
                {
                    errors.Add(path + " uses category 'ammunition' but has no ammunition block.");
                }

                return;
            }

            if (!isAmmunition)
            {
                errors.Add(path + " has an ammunition block but does not use category 'ammunition'.");
            }

            ValidateIdentifier(
                ammunition.AmmunitionFamily,
                path + ".ammunition.ammunitionFamily",
                errors);
            ValidateReference(
                ammunition.AmmunitionFamily,
                combat.AmmunitionFamilyIds,
                path + ".ammunition.ammunitionFamily",
                "ammunition family",
                errors);
            if (ammunition.DamageBonusPercent < 0
                || ammunition.DamageBonusPercent > CombatContentRules.MaximumAmmunitionDamageBonusPercent)
            {
                errors.Add(
                    path + ".ammunition.damageBonusPercent must be between 0 and "
                    + CombatContentRules.MaximumAmmunitionDamageBonusPercent.ToString(
                        CultureInfo.InvariantCulture)
                    + ".");
            }
        }

        // A stack can only be equipped into a slot that holds more than one unit.
        private static void ValidateEquipmentStacking(
            ItemDefinitionAuthoringEntry definition,
            IReadOnlyDictionary<string, ItemEquipmentSlotEntry> equipmentSlots,
            string path,
            ICollection<string> errors)
        {
            if (definition.EquipmentSlots == null || definition.MaximumStackSize <= 1)
            {
                return;
            }

            foreach (var slotId in definition.EquipmentSlots)
            {
                if (slotId != null
                    && equipmentSlots.TryGetValue(slotId, out var slot)
                    && slot != null
                    && slot.MaximumQuantity <= 1)
                {
                    errors.Add(
                        path
                        + ".equipmentSlots references single-item slot '"
                        + slotId
                        + "' for a stackable definition.");
                }
            }
        }

        private static void ValidateBlockedEquipmentSlots(
            ItemDefinitionAuthoringEntry definition,
            string path,
            ICollection<string> errors)
        {
            if (definition.BlockedEquipmentSlots == null
                || definition.BlockedEquipmentSlots.Length == 0)
            {
                return;
            }

            if (definition.EquipmentSlots == null || definition.EquipmentSlots.Length == 0)
            {
                errors.Add(path + ".blockedEquipmentSlots requires equipment compatibility.");
                return;
            }

            foreach (var blocked in definition.BlockedEquipmentSlots)
            {
                if (Contains(definition.EquipmentSlots, blocked))
                {
                    errors.Add(
                        path
                        + ".blockedEquipmentSlots must not contain compatible slot '"
                        + blocked
                        + "'.");
                }
            }
        }

        private static void ValidateBagDefinition(
            ItemDefinitionAuthoringEntry definition,
            string path,
            ISet<string> tagIds,
            ICollection<string> errors)
        {
            var hasBagEquipmentSlot = Contains(
                definition.EquipmentSlots,
                ItemEquipmentSlotIds.Bag);
            if (definition.Bag == null)
            {
                if (string.Equals(
                    definition.Category,
                    ItemCategoryIds.Bag,
                    StringComparison.Ordinal))
                {
                    errors.Add(path + " uses category 'bag' but has no Bag layout.");
                }

                if (hasBagEquipmentSlot)
                {
                    errors.Add(path + " uses the Bag equipment slot but has no Bag layout.");
                }

                return;
            }

            if (!string.Equals(
                definition.Category,
                ItemCategoryIds.Bag,
                StringComparison.Ordinal))
            {
                errors.Add(path + " has a Bag layout but does not use category 'bag'.");
            }

            if (definition.MaximumStackSize != 1)
            {
                errors.Add(path + " Bag definitions must be non-stackable.");
            }

            if (definition.EquipmentSlots == null
                || definition.EquipmentSlots.Length != 1
                || !hasBagEquipmentSlot)
            {
                errors.Add(path + " Bag definitions must use only the Bag equipment slot.");
            }

            if (definition.BlockedEquipmentSlots != null && definition.BlockedEquipmentSlots.Length > 0)
            {
                errors.Add(path + " Bag definitions cannot block equipment slots.");
            }

            if (definition.Bag.CarryCapacityBonus < 0)
            {
                errors.Add(path + ".bag.carryCapacityBonus must not be negative.");
            }

            var slots = definition.Bag.Slots;
            if (slots == null || slots.Length == 0)
            {
                errors.Add(path + ".bag.slots must contain at least one slot.");
                return;
            }

            var slotIndices = new HashSet<int>();
            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                var slotPath = path + ".bag.slots[" + index + "]";
                if (slot == null)
                {
                    errors.Add(slotPath + " must not be null.");
                    continue;
                }

                if (slot.Index < 0)
                {
                    errors.Add(slotPath + ".index must not be negative.");
                }
                else if (!slotIndices.Add(slot.Index))
                {
                    errors.Add(path + ".bag.slots contains duplicate index " + slot.Index + ".");
                }

                var recognizedKind = string.Equals(
                        slot.Kind,
                        BagSlotKindIds.General,
                        StringComparison.Ordinal)
                    || string.Equals(
                        slot.Kind,
                        BagSlotKindIds.Specialized,
                        StringComparison.Ordinal);
                if (!recognizedKind)
                {
                    errors.Add(slotPath + ".kind is unsupported.");
                }

                ValidateReferenceList(
                    slot.AcceptedTags,
                    tagIds,
                    slotPath + ".acceptedTags",
                    "tag",
                    errors);
                var acceptedTagCount = slot.AcceptedTags == null
                    ? 0
                    : slot.AcceptedTags.Length;
                if (acceptedTagCount > ItemCatalogFormat.MaximumAcceptedTagsPerSlot)
                {
                    errors.Add(
                        slotPath + ".acceptedTags must not contain more than "
                        + ItemCatalogFormat.MaximumAcceptedTagsPerSlot
                        + " tags.");
                }

                if (string.Equals(slot.Kind, BagSlotKindIds.General, StringComparison.Ordinal)
                    && acceptedTagCount != 0)
                {
                    errors.Add(slotPath + " general slots cannot configure accepted tags.");
                }

                if (string.Equals(
                        slot.Kind,
                        BagSlotKindIds.Specialized,
                        StringComparison.Ordinal)
                    && acceptedTagCount == 0)
                {
                    errors.Add(slotPath + " specialized slots require accepted tags.");
                }
            }

            for (var expectedIndex = 0; expectedIndex < slots.Length; expectedIndex++)
            {
                if (!slotIndices.Contains(expectedIndex))
                {
                    errors.Add(
                        path
                        + ".bag.slots must use contiguous stable indices starting at zero.");
                    break;
                }
            }
        }

        // Offers must be purchasable items with a value, and every sale rate stays below every
        // purchase rate, so no buy and sell loop between vendors can create currency.
        private static void ValidateVendors(
            VendorProfileAuthoringEntry[] vendors,
            ItemDefinitionAuthoringEntry[] definitions,
            ISet<string> categoryIds,
            ICollection<string> errors)
        {
            if (vendors == null)
            {
                errors.Add("vendors is required.");
                return;
            }

            var definitionsById = new Dictionary<string, ItemDefinitionAuthoringEntry>(StringComparer.Ordinal);
            foreach (var definition in definitions ?? Array.Empty<ItemDefinitionAuthoringEntry>())
            {
                if (definition?.Id != null && !definitionsById.ContainsKey(definition.Id))
                {
                    definitionsById.Add(definition.Id, definition);
                }
            }

            var vendorIds = new HashSet<string>(StringComparer.Ordinal);
            var highestSaleRate = -1;
            var lowestPurchaseRate = int.MaxValue;
            for (var index = 0; index < vendors.Length; index++)
            {
                var vendor = vendors[index];
                var path = "vendors[" + index + "]";
                if (vendor == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(vendor.Id, path + ".id", errors);
                ValidateDisplayName(vendor.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(vendor.Id) && !vendorIds.Add(vendor.Id))
                {
                    errors.Add("vendors contains duplicate id '" + vendor.Id + "'.");
                }

                if (vendor.PurchasePriceBasisPoints <= 0
                    || vendor.PurchasePriceBasisPoints > VendorPricing.MaximumPriceBasisPoints)
                {
                    errors.Add(
                        path + ".purchasePriceBasisPoints must be between 1 and "
                        + VendorPricing.MaximumPriceBasisPoints.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }
                else
                {
                    lowestPurchaseRate = Math.Min(lowestPurchaseRate, vendor.PurchasePriceBasisPoints);
                }

                if (vendor.SalePriceBasisPoints < 0
                    || vendor.SalePriceBasisPoints > VendorPricing.MaximumPriceBasisPoints)
                {
                    errors.Add(
                        path + ".salePriceBasisPoints is required and must be between 0 and "
                        + VendorPricing.MaximumPriceBasisPoints.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }
                else
                {
                    highestSaleRate = Math.Max(highestSaleRate, vendor.SalePriceBasisPoints);
                }

                ValidateReferenceList(
                    vendor.SaleCategories,
                    categoryIds,
                    path + ".saleCategories",
                    "category",
                    errors);
                if (Contains(vendor.SaleCategories, ItemCategoryIds.QuestItem))
                {
                    errors.Add(
                        path + ".saleCategories cannot contain '" + ItemCategoryIds.QuestItem + "'.");
                }

                ValidateVendorOffers(vendor, path, definitionsById, errors);
                if (vendor.Offers != null
                    && vendor.Offers.Length == 0
                    && vendor.SaleCategories != null
                    && vendor.SaleCategories.Length == 0)
                {
                    errors.Add(path + " must offer items or buy at least one category.");
                }
            }

            if (highestSaleRate >= 0
                && lowestPurchaseRate != int.MaxValue
                && highestSaleRate >= lowestPurchaseRate)
            {
                errors.Add(
                    "vendors must keep every salePriceBasisPoints below every "
                    + "purchasePriceBasisPoints, so buying and selling can never create currency.");
            }
        }

        private static void ValidateVendorOffers(
            VendorProfileAuthoringEntry vendor,
            string path,
            IReadOnlyDictionary<string, ItemDefinitionAuthoringEntry> definitionsById,
            ICollection<string> errors)
        {
            if (vendor.Offers == null)
            {
                errors.Add(path + ".offers is required.");
                return;
            }

            if (vendor.Offers.Length > ItemCatalogFormat.MaximumOffersPerVendor)
            {
                errors.Add(
                    path + ".offers must not contain more than "
                    + ItemCatalogFormat.MaximumOffersPerVendor
                    + " offers.");
            }

            var offered = new HashSet<string>(StringComparer.Ordinal);
            for (var offerIndex = 0; offerIndex < vendor.Offers.Length; offerIndex++)
            {
                var offer = vendor.Offers[offerIndex];
                var offerPath = path + ".offers[" + offerIndex + "]";
                if (offer == null)
                {
                    errors.Add(offerPath + " must not be null.");
                    continue;
                }

                ValidateIdentifier(offer.DefinitionId, offerPath + ".definitionId", errors);
                if (string.IsNullOrWhiteSpace(offer.DefinitionId))
                {
                    continue;
                }

                if (!offered.Add(offer.DefinitionId))
                {
                    errors.Add(
                        path + ".offers contains duplicate definition '" + offer.DefinitionId + "'.");
                }

                if (!definitionsById.TryGetValue(offer.DefinitionId, out var definition))
                {
                    errors.Add(
                        offerPath + ".definitionId references unknown definition '"
                        + offer.DefinitionId
                        + "'.");
                    continue;
                }

                if (definition.Value <= 0)
                {
                    errors.Add(offerPath + " must offer a definition with a positive value.");
                }

                if (definition.DefaultPolicies != null && definition.DefaultPolicies.Length > 0)
                {
                    errors.Add(offerPath + " cannot offer a definition with default policies.");
                }

                if (string.Equals(definition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal))
                {
                    errors.Add(offerPath + " cannot offer a quest item.");
                }
            }
        }

        private static HashSet<string> ValidateSecureContainerTiers(
            SecureContainerTierAuthoringEntry[] tiers,
            string baseTierId,
            ICollection<string> errors)
        {
            var tierIds = new HashSet<string>(StringComparer.Ordinal);
            if (tiers == null || tiers.Length == 0)
            {
                errors.Add("secureContainerTiers must contain at least one entry.");
                return tierIds;
            }

            for (var index = 0; index < tiers.Length; index++)
            {
                var tier = tiers[index];
                var path = "secureContainerTiers[" + index + "]";
                if (tier == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(tier.Id, path + ".id", errors);
                ValidateDisplayName(tier.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(tier.Id) && !tierIds.Add(tier.Id))
                {
                    errors.Add(
                        "secureContainerTiers contains duplicate id '" + tier.Id + "'.");
                }

                if (tier.SlotCapacity <= 0
                    || tier.SlotCapacity > MaximumContainerSlotCapacity)
                {
                    errors.Add(
                        path
                        + ".slotCapacity must be between 1 and "
                        + MaximumContainerSlotCapacity
                        + ".");
                }
            }

            if (!string.IsNullOrWhiteSpace(baseTierId) && !tierIds.Contains(baseTierId))
            {
                errors.Add("baseSecureContainerTierId references an unknown tier.");
            }

            return tierIds;
        }

        private static void ValidateRetiredIds(
            ItemCatalogRetiredIdsEntry retired,
            ISet<string> categoryIds,
            ISet<string> tagIds,
            ISet<string> equipmentSlotIds,
            ISet<string> definitionIds,
            ISet<string> tierIds,
            ICollection<string> errors)
        {
            if (retired == null)
            {
                errors.Add("retired is required.");
                return;
            }

            ValidateRetiredList(retired.Categories, categoryIds, "retired.categories", errors);
            ValidateRetiredList(retired.Tags, tagIds, "retired.tags", errors);
            ValidateRetiredList(
                retired.EquipmentSlots,
                equipmentSlotIds,
                "retired.equipmentSlots",
                errors);
            ValidateRetiredList(retired.Definitions, definitionIds, "retired.definitions", errors);
            ValidateRetiredList(
                retired.SecureContainerTiers,
                tierIds,
                "retired.secureContainerTiers",
                errors);
        }

        private static void ValidateRetiredList(
            string[] retiredIds,
            ISet<string> activeIds,
            string path,
            ICollection<string> errors)
        {
            if (retiredIds == null)
            {
                errors.Add(path + " is required.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < retiredIds.Length; index++)
            {
                var id = retiredIds[index];
                ValidateIdentifier(id, path + "[" + index + "]", errors);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!seen.Add(id))
                {
                    errors.Add(path + " contains duplicate id '" + id + "'.");
                }

                if (activeIds.Contains(id))
                {
                    errors.Add(path + "[" + index + "] retired id '" + id + "' cannot be reused.");
                }
            }
        }

        private static void ValidateDefaultPolicies(
            string[] policies,
            string definitionPath,
            ICollection<string> errors)
        {
            if (policies == null)
            {
                errors.Add(definitionPath + ".defaultPolicies is required.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < policies.Length; index++)
            {
                var policy = policies[index];
                ValidateIdentifier(
                    policy,
                    definitionPath + ".defaultPolicies[" + index + "]",
                    errors);
                if (!string.IsNullOrWhiteSpace(policy) && !seen.Add(policy))
                {
                    errors.Add(
                        definitionPath + ".defaultPolicies contains duplicate id '" + policy + "'.");
                }

                if (!string.IsNullOrWhiteSpace(policy)
                    && !KnownDefaultPolicies.Contains(policy))
                {
                    errors.Add(
                        definitionPath + ".defaultPolicies references unsupported policy '"
                        + policy
                        + "'.");
                }
            }
        }

        private static void ValidateReferenceList(
            string[] references,
            ISet<string> knownIds,
            string path,
            string referenceKind,
            ICollection<string> errors)
        {
            if (references == null)
            {
                errors.Add(path + " is required.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < references.Length; index++)
            {
                var value = references[index];
                ValidateIdentifier(value, path + "[" + index + "]", errors);
                if (!string.IsNullOrWhiteSpace(value) && !seen.Add(value))
                {
                    errors.Add(path + " contains duplicate id '" + value + "'.");
                }

                ValidateReference(
                    value,
                    knownIds,
                    path + "[" + index + "]",
                    referenceKind,
                    errors);
            }
        }

        private static void ValidateReference(
            string value,
            ISet<string> knownIds,
            string path,
            string referenceKind,
            ICollection<string> errors)
        {
            if (!string.IsNullOrWhiteSpace(value) && !knownIds.Contains(value))
            {
                errors.Add(path + " references unknown " + referenceKind + " '" + value + "'.");
            }
        }

        private static void ValidateIdentifier(
            string value,
            string path,
            ICollection<string> errors,
            int maximumLength = MaximumIdentifierLength)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > maximumLength
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || !IsLowerAsciiLetter(value[0])
                || !IsLowerAsciiLetterOrDigit(value[value.Length - 1]))
            {
                errors.Add(path + " must be a canonical lowercase identifier.");
                return;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!IsLowerAsciiLetterOrDigit(character)
                    && character != '-'
                    && character != '_'
                    && character != '.')
                {
                    errors.Add(path + " must be a canonical lowercase identifier.");
                    return;
                }
            }
        }

        private static void ValidateDisplayName(
            string value,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MaximumDisplayNameLength
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                errors.Add(
                    path
                    + " is required, must be trimmed, and must not exceed "
                    + MaximumDisplayNameLength
                    + " characters.");
            }
        }

        private static ItemCatalogIdentityEntry[] CompileIdentities(
            IEnumerable<ItemCatalogIdentityEntry> entries)
        {
            return entries
                .Select(entry => new ItemCatalogIdentityEntry
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName
                })
                .OrderBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
        }

        private static ItemDefinition CompileDefinition(
            ItemDefinitionAuthoringEntry authoring)
        {
            var definition = new ItemDefinition
            {
                Id = authoring.Id,
                DisplayName = authoring.DisplayName,
                Category = authoring.Category,
                Tags = SortStrings(authoring.Tags),
                ToolKinds = SortStrings(authoring.ToolKinds),
                UnitWeight = authoring.UnitWeight,
                Value = authoring.Value,
                MaximumStackSize = authoring.MaximumStackSize,
                EquipmentSlots = SortStrings(authoring.EquipmentSlots),
                BlockedEquipmentSlots = SortStrings(authoring.BlockedEquipmentSlots),
                PlayerDestroyable = authoring.PlayerDestroyable.Value,
                LocationEligibility = new ItemLocationEligibility
                {
                    SecureContainer = authoring.LocationEligibility.SecureContainer.Value
                },
                DefaultPolicies = SortStrings(authoring.DefaultPolicies),
                Bag = CompileBagDefinition(authoring.Bag),
                RequiredCharacterLevel = authoring.RequiredCharacterLevel,
                Weapon = authoring.Weapon == null
                    ? null
                    : new ItemWeaponEntry
                    {
                        WeaponFamily = authoring.Weapon.WeaponFamily,
                        DamageType = authoring.Weapon.DamageType,
                        MinimumDamage = authoring.Weapon.MinimumDamage,
                        MaximumDamage = authoring.Weapon.MaximumDamage,
                        AttackIntervalSeconds = authoring.Weapon.AttackIntervalSeconds,
                        Range = authoring.Weapon.Range,
                        ManaCost = authoring.Weapon.ManaCost
                    },
                Ammunition = authoring.Ammunition == null
                    ? null
                    : new ItemAmmunitionEntry
                    {
                        AmmunitionFamily = authoring.Ammunition.AmmunitionFamily,
                        DamageBonusPercent = authoring.Ammunition.DamageBonusPercent
                    },
                StructuralFingerprint = string.Empty
            };
            definition.StructuralFingerprint = ComputeDefinitionStructuralFingerprint(definition);
            return definition;
        }

        private static BagDefinition CompileBagDefinition(BagDefinitionAuthoringEntry authoring)
        {
            if (authoring == null)
            {
                return null;
            }

            return new BagDefinition
            {
                CarryCapacityBonus = authoring.CarryCapacityBonus,
                Slots = authoring.Slots
                    .Select(slot => new BagSlotDefinition
                    {
                        Index = slot.Index,
                        Kind = slot.Kind,
                        AcceptedTags = SortStrings(slot.AcceptedTags)
                    })
                    .OrderBy(slot => slot.Index)
                    .ToArray()
            };
        }

        private static SecureContainerTierDefinition CompileSecureContainerTier(
            SecureContainerTierAuthoringEntry authoring)
        {
            var tier = new SecureContainerTierDefinition
            {
                Id = authoring.Id,
                DisplayName = authoring.DisplayName,
                SlotCapacity = authoring.SlotCapacity,
                StructuralFingerprint = string.Empty
            };
            tier.StructuralFingerprint = ComputeSecureTierStructuralFingerprint(tier);
            return tier;
        }

        private static VendorProfileDefinition CompileVendor(VendorProfileAuthoringEntry authoring)
        {
            return new VendorProfileDefinition
            {
                Id = authoring.Id,
                DisplayName = authoring.DisplayName,
                Offers = authoring.Offers
                    .Select(offer => new VendorOfferEntry { DefinitionId = offer.DefinitionId })
                    .ToArray(),
                PurchasePriceBasisPoints = authoring.PurchasePriceBasisPoints,
                SalePriceBasisPoints = authoring.SalePriceBasisPoints,
                SaleCategories = SortStrings(authoring.SaleCategories)
            };
        }

        private static string[] SortStrings(IEnumerable<string> values)
        {
            return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static string ComputeDefinitionStructuralFingerprint(
            ItemDefinition definition)
        {
            return ComputeHash(writer =>
            {
                writer.Write(ItemCatalogFormat.StructuralFingerprintVersion);
                writer.Write(definition.Id);
                writer.Write(definition.Category);
                WriteStrings(writer, definition.Tags);
                writer.Write(definition.UnitWeight);
                writer.Write(definition.MaximumStackSize);
                WriteStrings(writer, definition.EquipmentSlots);
                WriteStrings(writer, definition.BlockedEquipmentSlots);
                writer.Write(definition.PlayerDestroyable);
                writer.Write(definition.LocationEligibility.SecureContainer);
                WriteStrings(writer, definition.DefaultPolicies);
                writer.Write(definition.Bag != null);
                if (definition.Bag != null)
                {
                    writer.Write(definition.Bag.CarryCapacityBonus);
                    writer.Write(definition.Bag.Slots.Length);
                    foreach (var slot in definition.Bag.Slots)
                    {
                        writer.Write(slot.Index);
                        writer.Write(slot.Kind);
                        WriteStrings(writer, slot.AcceptedTags);
                    }
                }
            });
        }

        private static string ComputeSecureTierStructuralFingerprint(
            SecureContainerTierDefinition tier)
        {
            return ComputeHash(writer =>
            {
                writer.Write(ItemCatalogFormat.StructuralFingerprintVersion);
                writer.Write(tier.Id);
                writer.Write(tier.SlotCapacity);
            });
        }

        private static string ComputeCatalogRevision(ItemCatalogRuntimeDocument catalog)
        {
            return ComputeHash(writer =>
            {
                writer.Write(catalog.FormatVersion);
                writer.Write(catalog.CatalogId);
                writer.Write(catalog.BaseSecureContainerTierId);
                writer.Write(catalog.Character.BaseCarryCapacity);
                writer.Write(catalog.Character.InventorySlotCount);
                writer.Write(catalog.Character.BankSlotCount);
                writer.Write(catalog.Economy.CurrencyDisplayName);
                writer.Write(catalog.Economy.StartingBalance);
                writer.Write(catalog.Progression.MaximumCharacterLevel);
                WriteIdentities(writer, catalog.Categories);
                WriteIdentities(writer, catalog.Tags);
                WriteIdentities(writer, catalog.ToolKinds);
                writer.Write(catalog.EquipmentSlots.Length);
                foreach (var slot in catalog.EquipmentSlots)
                {
                    writer.Write(slot.Id);
                    writer.Write(slot.DisplayName);
                    writer.Write(slot.MaximumQuantity);
                }

                writer.Write(catalog.WeaponFamilies.Length);
                foreach (var family in catalog.WeaponFamilies)
                {
                    writer.Write(family.Id);
                    writer.Write(family.DisplayName);
                    writer.Write(family.CombatStyle);
                    writer.Write(family.AmmunitionFamily);
                    writer.Write(family.ProjectileSpeed);
                }

                WriteIdentities(writer, catalog.AmmunitionFamilies);
                writer.Write(catalog.Definitions.Length);
                foreach (var definition in catalog.Definitions)
                {
                    writer.Write(definition.Id);
                    writer.Write(definition.DisplayName);
                    writer.Write(definition.StructuralFingerprint);
                    writer.Write(definition.Value);
                    WriteStrings(writer, definition.ToolKinds);
                    WriteCombatData(writer, definition);
                }

                writer.Write(catalog.SecureContainerTiers.Length);
                foreach (var tier in catalog.SecureContainerTiers)
                {
                    writer.Write(tier.Id);
                    writer.Write(tier.DisplayName);
                    writer.Write(tier.StructuralFingerprint);
                }

                writer.Write(catalog.Vendors.Length);
                foreach (var vendor in catalog.Vendors)
                {
                    writer.Write(vendor.Id);
                    writer.Write(vendor.DisplayName);
                    writer.Write(vendor.Offers.Length);
                    foreach (var offer in vendor.Offers)
                    {
                        writer.Write(offer.DefinitionId);
                    }

                    writer.Write(vendor.PurchasePriceBasisPoints);
                    writer.Write(vendor.SalePriceBasisPoints);
                    WriteStrings(writer, vendor.SaleCategories);
                }

                WriteStrings(writer, catalog.Retired.Categories);
                WriteStrings(writer, catalog.Retired.Tags);
                WriteStrings(writer, catalog.Retired.EquipmentSlots);
                WriteStrings(writer, catalog.Retired.Definitions);
                WriteStrings(writer, catalog.Retired.SecureContainerTiers);
            });
        }

        // Combat data and level requirements change the catalog revision but never custody,
        // so they stay out of the structural fingerprint.
        private static void WriteCombatData(BinaryWriter writer, ItemDefinition definition)
        {
            writer.Write(definition.RequiredCharacterLevel);
            writer.Write(definition.Weapon != null);
            if (definition.Weapon != null)
            {
                writer.Write(definition.Weapon.WeaponFamily);
                writer.Write(definition.Weapon.DamageType);
                writer.Write(definition.Weapon.MinimumDamage);
                writer.Write(definition.Weapon.MaximumDamage);
                writer.Write(definition.Weapon.AttackIntervalSeconds);
                writer.Write(definition.Weapon.Range);
                writer.Write(definition.Weapon.ManaCost);
            }

            writer.Write(definition.Ammunition != null);
            if (definition.Ammunition != null)
            {
                writer.Write(definition.Ammunition.AmmunitionFamily);
                writer.Write(definition.Ammunition.DamageBonusPercent);
            }
        }

        private static void WriteIdentities(
            BinaryWriter writer,
            ItemCatalogIdentityEntry[] entries)
        {
            writer.Write(entries.Length);
            foreach (var entry in entries)
            {
                writer.Write(entry.Id);
                writer.Write(entry.DisplayName);
            }
        }

        private static void WriteStrings(BinaryWriter writer, string[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }

        private static string ComputeHash(Action<BinaryWriter> write)
        {
            byte[] data;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                write(writer);
                writer.Flush();
                data = stream.ToArray();
            }

            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(data);
            }

            var builder = new StringBuilder(hash.Length * 2);
            for (var index = 0; index < hash.Length; index++)
            {
                builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        // The weapon families whose attacks are Magic and cost mana.
        private static HashSet<string> MagicWeaponFamilyIds(ItemWeaponFamilyEntry[] families)
        {
            return new HashSet<string>(
                (families ?? Array.Empty<ItemWeaponFamilyEntry>())
                    .Where(family => family != null
                        && family.Id != null
                        && string.Equals(family.CombatStyle, CombatStyleIds.Magic, StringComparison.Ordinal))
                    .Select(family => family.Id),
                StringComparer.Ordinal);
        }

        private readonly struct CombatReferences
        {
            public CombatReferences(
                ISet<string> weaponFamilyIds,
                ISet<string> magicWeaponFamilyIds,
                ISet<string> ammunitionFamilyIds,
                int maximumCharacterLevel)
            {
                WeaponFamilyIds = weaponFamilyIds;
                MagicWeaponFamilyIds = magicWeaponFamilyIds;
                AmmunitionFamilyIds = ammunitionFamilyIds;
                MaximumCharacterLevel = maximumCharacterLevel;
            }

            public ISet<string> WeaponFamilyIds { get; }

            public ISet<string> MagicWeaponFamilyIds { get; }

            public ISet<string> AmmunitionFamilyIds { get; }

            // 0 when the progression settings are invalid, which is reported separately.
            public int MaximumCharacterLevel { get; }
        }

        private static bool Contains(IEnumerable<string> values, string expected)
        {
            return values != null
                && values.Any(value => string.Equals(value, expected, StringComparison.Ordinal));
        }

        private static bool IsLowerAsciiLetter(char value)
        {
            return value >= 'a' && value <= 'z';
        }

        private static bool IsLowerAsciiLetterOrDigit(char value)
        {
            return IsLowerAsciiLetter(value) || (value >= '0' && value <= '9');
        }
    }
}
