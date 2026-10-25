using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Items
{
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

    // content/items/catalog.json: every authoring field except the definitions and the vendor
    // profiles, which live in one file each below definitions/ and vendors/.
    [Serializable]
    public sealed class ItemCatalogEditorCatalogHeader
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
        public ItemCatalogJsonSecureContainerTier[] secureContainerTiers;
        public ItemCatalogJsonRetired retired;
    }

    public static class ItemCatalogEditorJson
    {
        public static ItemCatalogEditorCatalogHeader DeserializeCatalogHeader(string json)
        {
            return Deserialize<ItemCatalogEditorCatalogHeader>(json, "Item catalog");
        }

        public static ItemCatalogJsonDefinition DeserializeDefinition(string json)
        {
            var definition = Deserialize<ItemCatalogJsonDefinition>(json, "Item definition");
            definition.ClearMissingPlaceholders();
            return definition;
        }

        public static ItemCatalogJsonVendor DeserializeVendor(string json)
        {
            return Deserialize<ItemCatalogJsonVendor>(json, "Vendor profile");
        }

        public static ItemCatalogEditorRuntimeDocument DeserializeRuntime(string json)
        {
            return Deserialize<ItemCatalogEditorRuntimeDocument>(json, "Item catalog runtime");
        }

        // The canonical file format is DataContract JSON: fields in ordinal order,
        // two-space indentation, and LF line endings.
        public static string SerializeCatalogHeader(ItemCatalogEditorCatalogHeader header)
        {
            return Serialize(header);
        }

        public static string SerializeDefinition(ItemCatalogJsonDefinition definition)
        {
            return Serialize(definition);
        }

        public static string SerializeVendor(ItemCatalogJsonVendor vendor)
        {
            return Serialize(vendor);
        }

        public static ItemCatalogJsonDocument Clone(ItemCatalogJsonDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var serializer = new DataContractJsonSerializer(typeof(ItemCatalogJsonDocument));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, document);
                stream.Position = 0;
                return (ItemCatalogJsonDocument)serializer.ReadObject(stream);
            }
        }

        public static ItemCatalogEditorRuntimeDocument Clone(
            ItemCatalogEditorRuntimeDocument document)
        {
            return DeserializeRuntime(JsonUtility.ToJson(document));
        }

        // Sorts every order-insensitive list so saved files never churn on reordering.
        public static void Normalize(ItemCatalogJsonDocument document)
        {
            document.categories = document.categories ?? Array.Empty<ItemCatalogJsonIdentity>();
            document.tags = document.tags ?? Array.Empty<ItemCatalogJsonIdentity>();
            document.toolKinds = document.toolKinds ?? Array.Empty<ItemCatalogJsonIdentity>();
            // Vendor offers keep their authored order: it is the order the vendor lists them.
            document.vendors = (document.vendors ?? Array.Empty<ItemCatalogJsonVendor>())
                .OrderBy(vendor => vendor.id, StringComparer.Ordinal)
                .ToArray();
            foreach (var vendor in document.vendors)
            {
                vendor.offers = vendor.offers ?? Array.Empty<ItemCatalogJsonVendorOffer>();
                vendor.saleCategories = SortStrings(vendor.saleCategories);
            }

            // Equipment slots keep their authored order: it is the display order.
            document.equipmentSlots = document.equipmentSlots
                ?? Array.Empty<ItemCatalogJsonEquipmentSlot>();
            document.weaponFamilies = document.weaponFamilies
                ?? Array.Empty<ItemCatalogJsonWeaponFamily>();
            document.ammunitionFamilies = document.ammunitionFamilies
                ?? Array.Empty<ItemCatalogJsonIdentity>();
            document.retired = document.retired ?? new ItemCatalogJsonRetired();
            document.retired.categories = SortStrings(document.retired.categories);
            document.retired.tags = SortStrings(document.retired.tags);
            document.retired.equipmentSlots = SortStrings(document.retired.equipmentSlots);
            document.retired.definitions = SortStrings(document.retired.definitions);
            document.retired.secureContainerTiers = SortStrings(document.retired.secureContainerTiers);
            document.secureContainerTiers = document.secureContainerTiers
                ?? Array.Empty<ItemCatalogJsonSecureContainerTier>();
            document.definitions = (document.definitions
                    ?? Array.Empty<ItemCatalogJsonDefinition>())
                .OrderBy(definition => definition.id, StringComparer.Ordinal)
                .ToArray();
            foreach (var definition in document.definitions)
            {
                definition.tags = SortStrings(definition.tags);
                definition.toolKinds = SortStrings(definition.toolKinds);
                definition.equipmentSlots = SortStrings(definition.equipmentSlots);
                definition.blockedEquipmentSlots = SortStrings(definition.blockedEquipmentSlots);
                definition.defaultPolicies = SortStrings(definition.defaultPolicies);
                if (definition.locationEligibility == null)
                {
                    definition.locationEligibility = new ItemCatalogJsonLocationEligibility();
                }

                if (definition.bag == null)
                {
                    continue;
                }

                definition.bag.slots = (definition.bag.slots
                        ?? Array.Empty<ItemCatalogJsonBagSlot>())
                    .OrderBy(slot => slot.index)
                    .ToArray();
                foreach (var slot in definition.bag.slots)
                {
                    slot.acceptedTags = SortStrings(slot.acceptedTags);
                }
            }
        }

        private static T Deserialize<T>(string json, string label)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException(label + " JSON is empty.");
            }

            try
            {
                return JsonUtility.FromJson<T>(json)
                    ?? throw new InvalidDataException(label + " JSON has no root object.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    label + " JSON is malformed: " + exception.Message,
                    exception);
            }
        }

        private static string Serialize<T>(T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                    stream,
                    new UTF8Encoding(false),
                    false,
                    true,
                    "  "))
                {
                    serializer.WriteObject(writer, value);
                    writer.Flush();
                }

                return NormalizeLineEndings(Encoding.UTF8.GetString(stream.ToArray())) + "\n";
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
    }

    // Reads and writes content/items: catalog.json plus one file per item definition and one
    // per vendor profile.
    public static class ItemCatalogEditorContent
    {
        public static ItemCatalogJsonDocument Load(string itemsDirectory)
        {
            var catalogPath = ContentLayout.CatalogPath(itemsDirectory);
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("Item catalog file is missing.", catalogPath);
            }

            var header = ItemCatalogEditorJson.DeserializeCatalogHeader(
                File.ReadAllText(catalogPath));
            var definitions = ContentLayout.ListDefinitionFiles(itemsDirectory)
                .Select(LoadDefinition)
                .ToArray();
            var vendors = ContentLayout.ListVendorFiles(itemsDirectory)
                .Select(LoadVendor)
                .ToArray();
            return new ItemCatalogJsonDocument
            {
                formatVersion = header.formatVersion,
                catalogId = header.catalogId,
                baseSecureContainerTierId = header.baseSecureContainerTierId,
                character = header.character,
                economy = header.economy,
                progression = header.progression,
                categories = header.categories,
                tags = header.tags,
                toolKinds = header.toolKinds,
                equipmentSlots = header.equipmentSlots,
                weaponFamilies = header.weaponFamilies,
                ammunitionFamilies = header.ammunitionFamilies,
                definitions = definitions,
                secureContainerTiers = header.secureContainerTiers,
                vendors = vendors,
                retired = header.retired
            };
        }

        // File text keyed by path relative to the items directory.
        public static Dictionary<string, string> CreateFiles(ItemCatalogJsonDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var normalized = ItemCatalogEditorJson.Clone(document);
            ItemCatalogEditorJson.Normalize(normalized);
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContentLayout.CatalogFileName] = ItemCatalogEditorJson.SerializeCatalogHeader(
                    new ItemCatalogEditorCatalogHeader
                    {
                        formatVersion = normalized.formatVersion,
                        catalogId = normalized.catalogId,
                        baseSecureContainerTierId = normalized.baseSecureContainerTierId,
                        character = normalized.character,
                        economy = normalized.economy,
                        progression = normalized.progression,
                        categories = normalized.categories,
                        tags = normalized.tags,
                        toolKinds = normalized.toolKinds,
                        equipmentSlots = normalized.equipmentSlots,
                        weaponFamilies = normalized.weaponFamilies,
                        ammunitionFamilies = normalized.ammunitionFamilies,
                        secureContainerTiers = normalized.secureContainerTiers,
                        retired = normalized.retired
                    })
            };
            foreach (var definition in normalized.definitions)
            {
                var path = Path.Combine(
                    ContentLayout.DefinitionsDirectoryName,
                    ContentLayout.DefinitionFileName(definition.id));
                if (files.ContainsKey(path))
                {
                    throw new InvalidDataException(
                        "Item definition id '" + definition.id + "' is used more than once.");
                }

                files.Add(path, ItemCatalogEditorJson.SerializeDefinition(definition));
            }

            foreach (var vendor in normalized.vendors)
            {
                var path = Path.Combine(
                    ContentLayout.VendorsDirectoryName,
                    ContentLayout.DefinitionFileName(vendor.id));
                if (files.ContainsKey(path))
                {
                    throw new InvalidDataException(
                        "Vendor profile id '" + vendor.id + "' is used more than once.");
                }

                files.Add(path, ItemCatalogEditorJson.SerializeVendor(vendor));
            }

            return files;
        }

        private static ItemCatalogJsonVendor LoadVendor(ContentDefinitionFile file)
        {
            ItemCatalogJsonVendor vendor;
            try
            {
                vendor = ItemCatalogEditorJson.DeserializeVendor(File.ReadAllText(file.Path));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(file.Path + ": " + exception.Message, exception);
            }

            if (!string.Equals(vendor.id, file.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Vendor profile file '" + file.Path + "' must contain id '" + file.Id + "'.");
            }

            return vendor;
        }

        private static ItemCatalogJsonDefinition LoadDefinition(ContentDefinitionFile file)
        {
            ItemCatalogJsonDefinition definition;
            try
            {
                definition = ItemCatalogEditorJson.DeserializeDefinition(
                    File.ReadAllText(file.Path));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(file.Path + ": " + exception.Message, exception);
            }

            if (!string.Equals(definition.id, file.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Item definition file '" + file.Path + "' must contain id '" + file.Id + "'.");
            }

            return definition;
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

    // The editable part of a workspace: what undo and redo restore.
    [Serializable]
    public sealed class ItemCatalogEditorSnapshot
    {
        public ItemCatalogJsonDocument authoring;
        public ItemPresentationCatalogDocument presentation;
    }

    public sealed class ItemCatalogEditorWorkspace
    {
        private readonly HashSet<string> bakedDefinitionIds;

        public ItemCatalogEditorWorkspace(
            ItemCatalogJsonDocument authoring,
            ItemCatalogEditorRuntimeDocument runtime,
            ItemPresentationCatalogDocument presentation)
        {
            Authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
            CurrentRuntime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            Authoring.definitions = Authoring.definitions
                ?? Array.Empty<ItemCatalogJsonDefinition>();
            Authoring.vendors = Authoring.vendors ?? Array.Empty<ItemCatalogJsonVendor>();
            Presentation.entries = Presentation.entries ?? Array.Empty<ItemPresentationEntry>();
            bakedDefinitionIds = new HashSet<string>(
                (CurrentRuntime.definitions ?? Array.Empty<ItemCatalogEditorRuntimeDefinition>())
                    .Select(definition => definition.id),
                StringComparer.Ordinal);
            SynchronizePresentationEntries();
        }

        public ItemCatalogJsonDocument Authoring { get; }

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

        // Replaces the editable content with a snapshot taken earlier, for undo and redo.
        public void RestoreContent(
            ItemCatalogJsonDocument authoring,
            ItemPresentationCatalogDocument presentation)
        {
            if (authoring == null)
            {
                throw new ArgumentNullException(nameof(authoring));
            }

            if (presentation == null)
            {
                throw new ArgumentNullException(nameof(presentation));
            }

            Authoring.formatVersion = authoring.formatVersion;
            Authoring.catalogId = authoring.catalogId;
            Authoring.baseSecureContainerTierId = authoring.baseSecureContainerTierId;
            Authoring.character = authoring.character;
            Authoring.economy = authoring.economy;
            Authoring.progression = authoring.progression;
            Authoring.retired = authoring.retired;
            Authoring.categories = authoring.categories;
            Authoring.tags = authoring.tags;
            Authoring.toolKinds = authoring.toolKinds;
            Authoring.equipmentSlots = authoring.equipmentSlots;
            Authoring.weaponFamilies = authoring.weaponFamilies;
            Authoring.ammunitionFamilies = authoring.ammunitionFamilies;
            Authoring.definitions = authoring.definitions ?? Array.Empty<ItemCatalogJsonDefinition>();
            Authoring.secureContainerTiers = authoring.secureContainerTiers;
            Authoring.vendors = authoring.vendors ?? Array.Empty<ItemCatalogJsonVendor>();
            Presentation.formatVersion = presentation.formatVersion;
            Presentation.sourceCatalogRevision = presentation.sourceCatalogRevision;
            Presentation.presentationRevision = presentation.presentationRevision;
            Presentation.entries = presentation.entries ?? Array.Empty<ItemPresentationEntry>();
        }

        public bool IsDefinitionIdLocked(string definitionId)
        {
            return !string.IsNullOrWhiteSpace(definitionId)
                && bakedDefinitionIds.Contains(definitionId);
        }

        // A retired id was released once, so no new definition may take it.
        public bool IsRetiredDefinitionId(string definitionId)
        {
            return Authoring.retired?.definitions != null
                && Authoring.retired.definitions.Contains(definitionId, StringComparer.Ordinal);
        }

        public ItemCatalogJsonDefinition CreateDefinition()
        {
            var definition = new ItemCatalogJsonDefinition
            {
                id = CreateUniqueId("new_item"),
                displayName = "New Item",
                category = Authoring.categories != null && Authoring.categories.Length > 0
                    ? Authoring.categories[0].id
                    : string.Empty,
                tags = Array.Empty<string>(),
                toolKinds = Array.Empty<string>(),
                unitWeight = 0,
                value = 0,
                maximumStackSize = 1,
                equipmentSlots = Array.Empty<string>(),
                blockedEquipmentSlots = Array.Empty<string>(),
                playerDestroyable = true,
                locationEligibility = new ItemCatalogJsonLocationEligibility(),
                defaultPolicies = Array.Empty<string>(),
                bag = null,
                requiredCharacterLevel = 1,
                weapon = null,
                ammunition = null
            };
            Authoring.definitions = Authoring.definitions.Concat(new[] { definition }).ToArray();
            AddPresentationEntry(definition);
            return definition;
        }

        public ItemCatalogJsonDefinition DuplicateDefinition(
            ItemCatalogJsonDefinition source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var copy = ItemCatalogEditorJson.Clone(new ItemCatalogJsonDocument
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
                    iconAddress = sourcePresentation == null
                        ? string.Empty
                        : sourcePresentation.iconAddress,
                    localizationKey = "items." + copy.id + ".name",
                    fallbackDisplayName = copy.displayName,
                    prefabPresentationKey = sourcePresentation == null
                        ? string.Empty
                        : sourcePresentation.prefabPresentationKey,
                    garments = (sourcePresentation?.garments ?? Array.Empty<string>()).ToArray(),
                    loweredGarments = (sourcePresentation?.loweredGarments ?? Array.Empty<string>()).ToArray(),
                    dyes = (sourcePresentation?.dyes ?? Array.Empty<string>()).ToArray()
                }
            }).ToArray();
            return copy;
        }

        public bool TryRenameDraftDefinition(
            ItemCatalogJsonDefinition definition,
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

            if (IsRetiredDefinitionId(newId))
            {
                error = "Id '" + newId + "' was retired and can never be reused.";
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
            ItemCatalogJsonDefinition definition,
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

        // A new vendor offers nothing yet, sells at the item value, buys at 40 percent, and buys
        // nothing until sale categories are chosen.
        public ItemCatalogJsonVendor CreateVendor()
        {
            var vendor = new ItemCatalogJsonVendor
            {
                id = CreateUniqueVendorId("vendor.new_vendor"),
                displayName = "New Vendor",
                offers = Array.Empty<ItemCatalogJsonVendorOffer>(),
                purchasePriceBasisPoints = VendorPricing.BasisPointsScale,
                salePriceBasisPoints = 4000,
                saleCategories = Array.Empty<string>()
            };
            Authoring.vendors = Authoring.vendors.Concat(new[] { vendor }).ToArray();
            return vendor;
        }

        // Actor content names vendor profiles, so a removed or renamed vendor fails the actor
        // compiler until the vendor capability is updated too.
        public void RemoveVendor(ItemCatalogJsonVendor vendor)
        {
            Authoring.vendors = Authoring.vendors
                .Where(candidate => !ReferenceEquals(candidate, vendor))
                .ToArray();
        }

        public bool TryRenameVendor(ItemCatalogJsonVendor vendor, string newId, out string error)
        {
            error = string.Empty;
            if (vendor == null)
            {
                error = "Vendor profile is missing.";
                return false;
            }

            if (Authoring.vendors.Any(candidate => !ReferenceEquals(candidate, vendor)
                && string.Equals(candidate.id, newId, StringComparison.Ordinal)))
            {
                error = "Another vendor profile already uses id '" + newId + "'.";
                return false;
            }

            vendor.id = newId;
            return true;
        }

        public ItemPresentationEntry FindPresentation(string definitionId)
        {
            return Presentation.entries.FirstOrDefault(entry => string.Equals(
                entry.definitionId,
                definitionId,
                StringComparison.Ordinal));
        }

        // Adds entries for new definitions and drops entries of definitions whose files were
        // removed, such as a retired definition.
        private void SynchronizePresentationEntries()
        {
            var definitionIds = new HashSet<string>(
                Authoring.definitions.Select(definition => definition.id),
                StringComparer.Ordinal);
            Presentation.entries = Presentation.entries
                .Where(entry => definitionIds.Contains(entry.definitionId))
                .ToArray();
            foreach (var definition in Authoring.definitions)
            {
                if (FindPresentation(definition.id) == null)
                {
                    AddPresentationEntry(definition);
                }
            }
        }

        private void AddPresentationEntry(ItemCatalogJsonDefinition definition)
        {
            Presentation.entries = Presentation.entries.Concat(new[]
            {
                new ItemPresentationEntry
                {
                    definitionId = definition.id,
                    iconAddress = string.Empty,
                    localizationKey = "items." + definition.id + ".name",
                    fallbackDisplayName = definition.displayName,
                    prefabPresentationKey = string.Empty
                }
            }).ToArray();
        }

        private string CreateUniqueVendorId(string preferredId)
        {
            var candidate = preferredId;
            var suffix = 2;
            while (Authoring.vendors.Any(vendor => string.Equals(vendor.id, candidate, StringComparison.Ordinal)))
            {
                candidate = preferredId + "_" + suffix;
                suffix++;
            }

            return candidate;
        }

        private string CreateUniqueId(string preferredId)
        {
            var candidate = preferredId;
            var suffix = 2;
            while (IsRetiredDefinitionId(candidate)
                || Authoring.definitions.Any(definition => string.Equals(
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
