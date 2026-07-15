using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShooterMmo.WorldData.Items
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
        private const int MaximumIdentifierLength = 128;
        private const int MaximumDisplayNameLength = 128;
        private const int MaximumContainerSlotCapacity = 65535;

        private static readonly string[] RequiredCategories =
        {
            ItemCategoryIds.Material,
            ItemCategoryIds.Medical,
            ItemCategoryIds.Weapon,
            ItemCategoryIds.Ammunition,
            ItemCategoryIds.Armor,
            ItemCategoryIds.Tool,
            ItemCategoryIds.Bag,
            ItemCategoryIds.QuestItem
        };

        private static readonly string[] RequiredTags =
        {
            ItemTagIds.Medical,
            ItemTagIds.Material,
            ItemTagIds.Ammunition
        };

        private static readonly string[] RequiredEquipmentSlots =
        {
            ItemEquipmentSlotIds.Head,
            ItemEquipmentSlotIds.BodyArmor,
            ItemEquipmentSlotIds.PrimaryWeapon,
            ItemEquipmentSlotIds.SecondaryWeapon,
            ItemEquipmentSlotIds.Tool,
            ItemEquipmentSlotIds.Ring1,
            ItemEquipmentSlotIds.Ring2,
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
                Categories = CompileIdentities(authoring.Categories),
                Tags = CompileIdentities(authoring.Tags),
                EquipmentSlots = CompileIdentities(authoring.EquipmentSlots),
                Definitions = authoring.Definitions
                    .Select(CompileDefinition)
                    .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                    .ToArray(),
                SecureContainerTiers = authoring.SecureContainerTiers
                    .Select(CompileSecureContainerTier)
                    .OrderBy(tier => tier.Id, StringComparer.Ordinal)
                    .ToArray()
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

            var categoryIds = ValidateIdentities(authoring.Categories, "categories", errors);
            var tagIds = ValidateIdentities(authoring.Tags, "tags", errors);
            var equipmentSlotIds = ValidateIdentities(
                authoring.EquipmentSlots,
                "equipmentSlots",
                errors);

            ValidateRequiredIds(categoryIds, RequiredCategories, "categories", errors);
            ValidateRequiredIds(tagIds, RequiredTags, "tags", errors);
            ValidateRequiredIds(
                equipmentSlotIds,
                RequiredEquipmentSlots,
                "equipmentSlots",
                errors);

            ValidateDefinitions(
                authoring.Definitions,
                categoryIds,
                tagIds,
                equipmentSlotIds,
                errors);
            ValidateSecureContainerTiers(
                authoring.SecureContainerTiers,
                authoring.BaseSecureContainerTierId,
                errors);

            if (errors.Count > 0)
            {
                throw new ItemCatalogValidationException(errors);
            }
        }

        private static HashSet<string> ValidateIdentities(
            ItemCatalogIdentityEntry[] entries,
            string collectionName,
            ICollection<string> errors)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (entries == null || entries.Length == 0)
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

                ValidateIdentifier(entry.Id, path + ".id", errors);
                ValidateDisplayName(entry.DisplayName, path + ".displayName", errors);
                if (!string.IsNullOrWhiteSpace(entry.Id) && !ids.Add(entry.Id))
                {
                    errors.Add(collectionName + " contains duplicate id '" + entry.Id + "'.");
                }
            }

            return ids;
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

        private static void ValidateDefinitions(
            ItemDefinitionAuthoringEntry[] definitions,
            ISet<string> categoryIds,
            ISet<string> tagIds,
            ISet<string> equipmentSlotIds,
            ICollection<string> errors)
        {
            if (definitions == null || definitions.Length == 0)
            {
                errors.Add("definitions must contain at least one entry.");
                return;
            }

            var definitionIds = new HashSet<string>(StringComparer.Ordinal);
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
                ValidateDefaultPolicies(definition.DefaultPolicies, path, errors);

                if (definition.UnitWeight < 0)
                {
                    errors.Add(path + ".unitWeight must not be negative.");
                }

                if (definition.MaximumStackSize <= 0)
                {
                    errors.Add(path + ".maximumStackSize must be positive.");
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

                var hasEquipmentCompatibility = definition.EquipmentSlots != null
                    && definition.EquipmentSlots.Length > 0;
                if (hasEquipmentCompatibility && definition.MaximumStackSize != 1)
                {
                    errors.Add(path + " equipment-compatible definitions must be non-stackable.");
                }

                ValidateBagDefinition(definition, path, tagIds, errors);

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

                if (Contains(definition.DefaultPolicies, ItemPolicyIds.Insured)
                    && (!hasEquipmentCompatibility || definition.MaximumStackSize != 1))
                {
                    errors.Add(path + " insured defaults require non-stackable equipment.");
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

        private static void ValidateSecureContainerTiers(
            SecureContainerTierAuthoringEntry[] tiers,
            string baseTierId,
            ICollection<string> errors)
        {
            if (tiers == null || tiers.Length == 0)
            {
                errors.Add("secureContainerTiers must contain at least one entry.");
                return;
            }

            var tierIds = new HashSet<string>(StringComparer.Ordinal);
            SecureContainerTierAuthoringEntry baseTier = null;
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

                if (string.Equals(tier.Id, baseTierId, StringComparison.Ordinal))
                {
                    baseTier = tier;
                }
            }

            if (baseTier == null)
            {
                errors.Add("baseSecureContainerTierId references an unknown tier.");
            }
            else if (baseTier.SlotCapacity != 4)
            {
                errors.Add("The base Secure Container tier must have exactly four slots.");
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
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MaximumIdentifierLength
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
                UnitWeight = authoring.UnitWeight,
                MaximumStackSize = authoring.MaximumStackSize,
                EquipmentSlots = SortStrings(authoring.EquipmentSlots),
                PlayerDestroyable = authoring.PlayerDestroyable.Value,
                LocationEligibility = new ItemLocationEligibility
                {
                    SecureContainer = authoring.LocationEligibility.SecureContainer.Value
                },
                DefaultPolicies = SortStrings(authoring.DefaultPolicies),
                Bag = CompileBagDefinition(authoring.Bag),
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

        private static string[] SortStrings(IEnumerable<string> values)
        {
            return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static string ComputeDefinitionStructuralFingerprint(
            ItemDefinition definition)
        {
            return ComputeHash(writer =>
            {
                writer.Write(ItemCatalogFormat.Version);
                writer.Write(definition.Id);
                writer.Write(definition.Category);
                WriteStrings(writer, definition.Tags);
                writer.Write(definition.UnitWeight);
                writer.Write(definition.MaximumStackSize);
                WriteStrings(writer, definition.EquipmentSlots);
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
                writer.Write(ItemCatalogFormat.Version);
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
                WriteIdentities(writer, catalog.Categories);
                WriteIdentities(writer, catalog.Tags);
                WriteIdentities(writer, catalog.EquipmentSlots);
                writer.Write(catalog.Definitions.Length);
                foreach (var definition in catalog.Definitions)
                {
                    writer.Write(definition.Id);
                    writer.Write(definition.DisplayName);
                    writer.Write(definition.StructuralFingerprint);
                }

                writer.Write(catalog.SecureContainerTiers.Length);
                foreach (var tier in catalog.SecureContainerTiers)
                {
                    writer.Write(tier.Id);
                    writer.Write(tier.DisplayName);
                    writer.Write(tier.StructuralFingerprint);
                }
            });
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
