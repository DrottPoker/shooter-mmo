using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using ShooterMmo.WorldData.Items;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Items
{
    [Serializable]
    public sealed class ItemCatalogEditorIdentity
    {
        public string id;
        public string displayName;
    }

    [Serializable]
    public sealed class ItemCatalogEditorLocationEligibility
    {
        public bool secureContainer;
    }

    [Serializable]
    public sealed class ItemCatalogEditorBagSlot
    {
        public int index;
        public string kind;
        public string[] acceptedTags;
    }

    [Serializable]
    public sealed class ItemCatalogEditorBagDefinition
    {
        public long carryCapacityBonus;
        public ItemCatalogEditorBagSlot[] slots;
    }

    [Serializable]
    public sealed class ItemCatalogEditorDefinition
    {
        public string id;
        public string displayName;
        public string category;
        public string[] tags;
        public long unitWeight;
        public int maximumStackSize;
        public string[] equipmentSlots;
        public bool playerDestroyable;
        public ItemCatalogEditorLocationEligibility locationEligibility;
        public string[] defaultPolicies;
        public ItemCatalogEditorBagDefinition bag;
    }

    [Serializable]
    public sealed class ItemCatalogEditorDocument
    {
        public int formatVersion;
        public string catalogId;
        public string baseSecureContainerTierId;
        public ItemCatalogEditorIdentity[] categories;
        public ItemCatalogEditorIdentity[] tags;
        public ItemCatalogEditorIdentity[] equipmentSlots;
        public ItemCatalogEditorDefinition[] definitions;
        public ItemCatalogEditorSecureContainerTier[] secureContainerTiers;

        public ItemCatalogAuthoringDocument ToDomainDocument()
        {
            return new ItemCatalogAuthoringDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                BaseSecureContainerTierId = baseSecureContainerTierId,
                Categories = ConvertIdentities(categories),
                Tags = ConvertIdentities(tags),
                EquipmentSlots = ConvertIdentities(equipmentSlots),
                Definitions = (definitions ?? Array.Empty<ItemCatalogEditorDefinition>())
                    .Select(ConvertDefinition)
                    .ToArray(),
                SecureContainerTiers = (secureContainerTiers
                        ?? Array.Empty<ItemCatalogEditorSecureContainerTier>())
                    .Select(tier => new SecureContainerTierAuthoringEntry
                    {
                        Id = tier.id,
                        DisplayName = tier.displayName,
                        SlotCapacity = tier.slotCapacity
                    })
                    .ToArray()
            };
        }

        private static ItemCatalogIdentityEntry[] ConvertIdentities(
            ItemCatalogEditorIdentity[] identities)
        {
            return (identities ?? Array.Empty<ItemCatalogEditorIdentity>())
                .Select(identity => new ItemCatalogIdentityEntry
                {
                    Id = identity.id,
                    DisplayName = identity.displayName
                })
                .ToArray();
        }

        private static ItemDefinitionAuthoringEntry ConvertDefinition(
            ItemCatalogEditorDefinition definition)
        {
            return new ItemDefinitionAuthoringEntry
            {
                Id = definition.id,
                DisplayName = definition.displayName,
                Category = definition.category,
                Tags = definition.tags ?? Array.Empty<string>(),
                UnitWeight = definition.unitWeight,
                MaximumStackSize = definition.maximumStackSize,
                EquipmentSlots = definition.equipmentSlots ?? Array.Empty<string>(),
                PlayerDestroyable = definition.playerDestroyable,
                LocationEligibility = definition.locationEligibility == null
                    ? null
                    : new ItemLocationEligibilityAuthoringEntry
                    {
                        SecureContainer = definition.locationEligibility.secureContainer
                    },
                DefaultPolicies = definition.defaultPolicies ?? Array.Empty<string>(),
                Bag = ConvertBag(definition.bag)
            };
        }

        private static BagDefinitionAuthoringEntry ConvertBag(
            ItemCatalogEditorBagDefinition bag)
        {
            if (bag == null)
            {
                return null;
            }

            return new BagDefinitionAuthoringEntry
            {
                CarryCapacityBonus = bag.carryCapacityBonus,
                Slots = (bag.slots ?? Array.Empty<ItemCatalogEditorBagSlot>())
                    .Select(slot => new BagSlotAuthoringEntry
                    {
                        Index = slot.index,
                        Kind = slot.kind,
                        AcceptedTags = slot.acceptedTags ?? Array.Empty<string>()
                    })
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class ItemCatalogEditorSecureContainerTier
    {
        public string id;
        public string displayName;
        public int slotCapacity;
    }

    [Serializable]
    public sealed class ItemCatalogEditorRuntimeDocument
    {
        public int formatVersion;
        public string catalogId;
        public string revision;
        public ItemCatalogEditorRuntimeDefinition[] definitions;

        public static ItemCatalogEditorRuntimeDocument FromDomain(
            ItemCatalogRuntimeDocument catalog)
        {
            return new ItemCatalogEditorRuntimeDocument
            {
                formatVersion = catalog.FormatVersion,
                catalogId = catalog.CatalogId,
                revision = catalog.Revision,
                definitions = catalog.Definitions.Select(definition =>
                    new ItemCatalogEditorRuntimeDefinition
                    {
                        id = definition.Id,
                        displayName = definition.DisplayName,
                        structuralFingerprint = definition.StructuralFingerprint
                    })
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class ItemCatalogEditorRuntimeDefinition
    {
        public string id;
        public string displayName;
        public string structuralFingerprint;
    }

    public static class ItemCatalogEditorJson
    {
        public static ItemCatalogEditorDocument DeserializeAuthoring(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Item catalog authoring JSON is empty.");
            }

            try
            {
                var document = JsonUtility.FromJson<ItemCatalogEditorDocument>(json)
                    ?? throw new InvalidDataException(
                        "Item catalog authoring JSON has no root object.");
                ClearMissingBagPlaceholders(document);
                return document;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Item catalog authoring JSON is malformed: " + exception.Message,
                    exception);
            }
        }

        public static ItemCatalogEditorRuntimeDocument DeserializeRuntime(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Item catalog runtime JSON is empty.");
            }

            try
            {
                return JsonUtility.FromJson<ItemCatalogEditorRuntimeDocument>(json)
                    ?? throw new InvalidDataException(
                        "Item catalog runtime JSON has no root object.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Item catalog runtime JSON is malformed: " + exception.Message,
                    exception);
            }
        }

        public static string SerializeAuthoring(ItemCatalogEditorDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var normalized = Clone(document);
            Normalize(normalized);
            var serializer = CreateAuthoringSerializer();
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                    stream,
                    new UTF8Encoding(false),
                    false,
                    true,
                    "  "))
                {
                    serializer.WriteObject(writer, normalized);
                    writer.Flush();
                }

                return NormalizeLineEndings(Encoding.UTF8.GetString(stream.ToArray())) + "\n";
            }
        }

        public static ItemCatalogEditorDocument Clone(ItemCatalogEditorDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var serializer = CreateAuthoringSerializer();
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, document);
                stream.Position = 0;
                return (ItemCatalogEditorDocument)serializer.ReadObject(stream);
            }
        }

        public static ItemCatalogEditorRuntimeDocument Clone(
            ItemCatalogEditorRuntimeDocument document)
        {
            return DeserializeRuntime(JsonUtility.ToJson(document));
        }

        private static void Normalize(ItemCatalogEditorDocument document)
        {
            document.categories = document.categories ?? Array.Empty<ItemCatalogEditorIdentity>();
            document.tags = document.tags ?? Array.Empty<ItemCatalogEditorIdentity>();
            document.equipmentSlots = document.equipmentSlots
                ?? Array.Empty<ItemCatalogEditorIdentity>();
            document.secureContainerTiers = document.secureContainerTiers
                ?? Array.Empty<ItemCatalogEditorSecureContainerTier>();
            document.definitions = (document.definitions
                    ?? Array.Empty<ItemCatalogEditorDefinition>())
                .OrderBy(definition => definition.id, StringComparer.Ordinal)
                .ToArray();
            foreach (var definition in document.definitions)
            {
                definition.tags = SortStrings(definition.tags);
                definition.equipmentSlots = SortStrings(definition.equipmentSlots);
                definition.defaultPolicies = SortStrings(definition.defaultPolicies);
                if (definition.locationEligibility == null)
                {
                    definition.locationEligibility = new ItemCatalogEditorLocationEligibility();
                }

                if (definition.bag == null)
                {
                    continue;
                }

                definition.bag.slots = (definition.bag.slots
                        ?? Array.Empty<ItemCatalogEditorBagSlot>())
                    .OrderBy(slot => slot.index)
                    .ToArray();
                foreach (var slot in definition.bag.slots)
                {
                    slot.acceptedTags = SortStrings(slot.acceptedTags);
                }
            }
        }

        private static void ClearMissingBagPlaceholders(ItemCatalogEditorDocument document)
        {
            foreach (var definition in document.definitions
                ?? Array.Empty<ItemCatalogEditorDefinition>())
            {
                if (definition.bag != null
                    && (definition.bag.slots == null || definition.bag.slots.Length == 0)
                    && !string.Equals(
                        definition.category,
                        ItemCategoryIds.Bag,
                        StringComparison.Ordinal))
                {
                    // JsonUtility materializes an empty nested class for a missing Bag field.
                    // Strict validation runs before canonical Editor content reaches this model.
                    definition.bag = null;
                }
            }
        }

        private static string[] SortStrings(string[] values)
        {
            return (values ?? Array.Empty<string>())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string NormalizeLineEndings(string value)
        {
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static DataContractJsonSerializer CreateAuthoringSerializer()
        {
            return new DataContractJsonSerializer(typeof(ItemCatalogEditorDocument));
        }
    }

    public enum ItemCatalogDefinitionChangeKind
    {
        Added = 1,
        DisplayOnly = 2,
        Structural = 3,
        Removed = 4
    }

    public sealed class ItemCatalogDefinitionChange
    {
        public ItemCatalogDefinitionChange(string definitionId, ItemCatalogDefinitionChangeKind kind)
        {
            DefinitionId = definitionId;
            Kind = kind;
        }

        public string DefinitionId { get; }

        public ItemCatalogDefinitionChangeKind Kind { get; }
    }

    public static class ItemCatalogChangeClassifier
    {
        public static ItemCatalogDefinitionChange[] Compare(
            ItemCatalogEditorRuntimeDocument current,
            ItemCatalogEditorRuntimeDocument candidate)
        {
            var currentDefinitions = (current == null
                    ? Array.Empty<ItemCatalogEditorRuntimeDefinition>()
                    : current.definitions ?? Array.Empty<ItemCatalogEditorRuntimeDefinition>())
                .ToDictionary(definition => definition.id, StringComparer.Ordinal);
            var candidateDefinitions = (candidate == null
                    ? Array.Empty<ItemCatalogEditorRuntimeDefinition>()
                    : candidate.definitions ?? Array.Empty<ItemCatalogEditorRuntimeDefinition>())
                .ToDictionary(definition => definition.id, StringComparer.Ordinal);
            var changes = new List<ItemCatalogDefinitionChange>();

            foreach (var pair in candidateDefinitions)
            {
                ItemCatalogEditorRuntimeDefinition previous;
                if (!currentDefinitions.TryGetValue(pair.Key, out previous))
                {
                    changes.Add(new ItemCatalogDefinitionChange(
                        pair.Key,
                        ItemCatalogDefinitionChangeKind.Added));
                    continue;
                }

                if (!string.Equals(
                    previous.structuralFingerprint,
                    pair.Value.structuralFingerprint,
                    StringComparison.Ordinal))
                {
                    changes.Add(new ItemCatalogDefinitionChange(
                        pair.Key,
                        ItemCatalogDefinitionChangeKind.Structural));
                    continue;
                }

                if (!string.Equals(
                    previous.displayName,
                    pair.Value.displayName,
                    StringComparison.Ordinal))
                {
                    changes.Add(new ItemCatalogDefinitionChange(
                        pair.Key,
                        ItemCatalogDefinitionChangeKind.DisplayOnly));
                }
            }

            foreach (var definitionId in currentDefinitions.Keys)
            {
                if (!candidateDefinitions.ContainsKey(definitionId))
                {
                    changes.Add(new ItemCatalogDefinitionChange(
                        definitionId,
                        ItemCatalogDefinitionChangeKind.Removed));
                }
            }

            return changes
                .OrderBy(change => change.DefinitionId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public sealed class ItemCatalogEditorWorkspace
    {
        private readonly HashSet<string> bakedDefinitionIds;

        public ItemCatalogEditorWorkspace(
            ItemCatalogEditorDocument authoring,
            ItemCatalogEditorRuntimeDocument runtime,
            ItemPresentationCatalogDocument presentation)
        {
            Authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
            CurrentRuntime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            Authoring.definitions = Authoring.definitions
                ?? Array.Empty<ItemCatalogEditorDefinition>();
            Presentation.entries = Presentation.entries ?? Array.Empty<ItemPresentationEntry>();
            bakedDefinitionIds = new HashSet<string>(
                (CurrentRuntime.definitions ?? Array.Empty<ItemCatalogEditorRuntimeDefinition>())
                    .Select(definition => definition.id),
                StringComparer.Ordinal);
            SynchronizePresentationEntries();
        }

        public ItemCatalogEditorDocument Authoring { get; }

        public ItemCatalogEditorRuntimeDocument CurrentRuntime { get; }

        public ItemPresentationCatalogDocument Presentation { get; }

        public void ApplyBakedRuntime(ItemCatalogEditorRuntimeDocument runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            CurrentRuntime.formatVersion = runtime.formatVersion;
            CurrentRuntime.catalogId = runtime.catalogId;
            CurrentRuntime.revision = runtime.revision;
            CurrentRuntime.definitions = runtime.definitions;
            bakedDefinitionIds.Clear();
            foreach (var definition in runtime.definitions
                ?? Array.Empty<ItemCatalogEditorRuntimeDefinition>())
            {
                bakedDefinitionIds.Add(definition.id);
            }
        }

        public bool IsDefinitionIdLocked(string definitionId)
        {
            return !string.IsNullOrWhiteSpace(definitionId)
                && bakedDefinitionIds.Contains(definitionId);
        }

        public ItemCatalogEditorDefinition CreateDefinition()
        {
            var definition = new ItemCatalogEditorDefinition
            {
                id = CreateUniqueId("new_item"),
                displayName = "New Item",
                category = Authoring.categories != null && Authoring.categories.Length > 0
                    ? Authoring.categories[0].id
                    : string.Empty,
                tags = Array.Empty<string>(),
                unitWeight = 0,
                maximumStackSize = 1,
                equipmentSlots = Array.Empty<string>(),
                playerDestroyable = true,
                locationEligibility = new ItemCatalogEditorLocationEligibility(),
                defaultPolicies = Array.Empty<string>(),
                bag = null
            };
            Authoring.definitions = Authoring.definitions.Concat(new[] { definition }).ToArray();
            AddPresentationEntry(definition);
            return definition;
        }

        public ItemCatalogEditorDefinition DuplicateDefinition(
            ItemCatalogEditorDefinition source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var copy = ItemCatalogEditorJson.Clone(new ItemCatalogEditorDocument
            {
                definitions = new[] { source }
            }).definitions[0];
            copy.id = CreateUniqueId(source.id + "_copy");
            copy.displayName = source.displayName + " Copy";
            Authoring.definitions = Authoring.definitions.Concat(new[] { copy }).ToArray();

            var sourcePresentation = FindPresentation(source.id);
            Presentation.entries = Presentation.entries.Concat(new[]
            {
                new ItemPresentationEntry
                {
                    definitionId = copy.id,
                    iconResourcePath = sourcePresentation == null
                        ? string.Empty
                        : sourcePresentation.iconResourcePath,
                    localizationKey = "items." + copy.id + ".name",
                    fallbackDisplayName = copy.displayName,
                    prefabPresentationKey = sourcePresentation == null
                        ? string.Empty
                        : sourcePresentation.prefabPresentationKey
                }
            }).ToArray();
            return copy;
        }

        public bool TryRenameDraftDefinition(
            ItemCatalogEditorDefinition definition,
            string newId,
            out string error)
        {
            error = string.Empty;
            if (definition == null)
            {
                error = "Definition is missing.";
                return false;
            }

            if (IsDefinitionIdLocked(definition.id))
            {
                error = "A baked definition id cannot be changed.";
                return false;
            }

            if (Authoring.definitions.Any(candidate => !ReferenceEquals(candidate, definition)
                && string.Equals(candidate.id, newId, StringComparison.Ordinal)))
            {
                error = "Another definition already uses id '" + newId + "'.";
                return false;
            }

            var presentation = FindPresentation(definition.id);
            definition.id = newId;
            if (presentation != null)
            {
                presentation.definitionId = newId;
                presentation.localizationKey = "items." + newId + ".name";
            }

            return true;
        }

        public bool TryRemoveDraftDefinition(
            ItemCatalogEditorDefinition definition,
            out string error)
        {
            error = string.Empty;
            if (definition == null)
            {
                error = "Definition is missing.";
                return false;
            }

            if (IsDefinitionIdLocked(definition.id))
            {
                error = "A baked definition cannot be removed.";
                return false;
            }

            Authoring.definitions = Authoring.definitions
                .Where(candidate => !ReferenceEquals(candidate, definition))
                .ToArray();
            Presentation.entries = Presentation.entries
                .Where(entry => !string.Equals(
                    entry.definitionId,
                    definition.id,
                    StringComparison.Ordinal))
                .ToArray();
            return true;
        }

        public ItemPresentationEntry FindPresentation(string definitionId)
        {
            return Presentation.entries.FirstOrDefault(entry => string.Equals(
                entry.definitionId,
                definitionId,
                StringComparison.Ordinal));
        }

        private void SynchronizePresentationEntries()
        {
            foreach (var definition in Authoring.definitions)
            {
                if (FindPresentation(definition.id) == null)
                {
                    AddPresentationEntry(definition);
                }
            }
        }

        private void AddPresentationEntry(ItemCatalogEditorDefinition definition)
        {
            Presentation.entries = Presentation.entries.Concat(new[]
            {
                new ItemPresentationEntry
                {
                    definitionId = definition.id,
                    iconResourcePath = string.Empty,
                    localizationKey = "items." + definition.id + ".name",
                    fallbackDisplayName = definition.displayName,
                    prefabPresentationKey = string.Empty
                }
            }).ToArray();
        }

        private string CreateUniqueId(string preferredId)
        {
            var candidate = preferredId;
            var suffix = 2;
            while (Authoring.definitions.Any(definition => string.Equals(
                definition.id,
                candidate,
                StringComparison.Ordinal)))
            {
                candidate = preferredId + "_" + suffix;
                suffix++;
            }

            return candidate;
        }
    }
}
