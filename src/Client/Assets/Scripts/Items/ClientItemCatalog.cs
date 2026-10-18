using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;

namespace Mmorpg.Client.Items
{
    public sealed class ClientItemCatalog
    {
        private readonly Dictionary<string, ItemDefinition> definitions;
        private readonly Dictionary<string, ItemEquipmentSlotEntry> equipmentSlots;

        private ClientItemCatalog(
            ItemCatalogRuntimeDocument gameplay,
            ItemPresentationCatalogIndex presentation)
        {
            Gameplay = gameplay;
            Presentation = presentation;
            definitions = gameplay.Definitions.ToDictionary(
                definition => definition.Id,
                definition => definition,
                StringComparer.Ordinal);
            equipmentSlots = gameplay.EquipmentSlots.ToDictionary(
                slot => slot.Id,
                slot => slot,
                StringComparer.Ordinal);
        }

        public ItemCatalogRuntimeDocument Gameplay { get; }

        public ItemPresentationCatalogIndex Presentation { get; }

        public string Revision
        {
            get { return Gameplay.Revision; }
        }

        public IReadOnlyCollection<ItemDefinition> Definitions
        {
            get { return definitions.Values; }
        }

        public static bool TryLoad(
            TextAsset gameplayAsset,
            out ClientItemCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (gameplayAsset == null)
            {
                error = "The bundled item gameplay catalog is missing.";
                return false;
            }

            if (!TryParseGameplay(gameplayAsset.text, out var gameplay, out error)
                || !ItemPresentationCatalogLoader.TryLoadOnce(
                    gameplay.Revision,
                    gameplay.Definitions.Select(definition => definition.Id),
                    out var presentation,
                    out error))
            {
                return false;
            }

            catalog = new ClientItemCatalog(gameplay, presentation);
            return true;
        }

        // Combines a verified gameplay catalog with a presentation index built for it, such as
        // a test catalog that has no bundled presentation.
        public static bool TryCreate(
            ItemCatalogRuntimeDocument gameplay,
            ItemPresentationCatalogIndex presentation,
            out ClientItemCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (gameplay == null || presentation == null)
            {
                error = "A gameplay catalog and its presentation are required.";
                return false;
            }

            if (!string.Equals(
                presentation.Catalog.sourceCatalogRevision,
                gameplay.Revision,
                StringComparison.Ordinal))
            {
                error = "The item presentation does not belong to the gameplay catalog revision.";
                return false;
            }

            catalog = new ClientItemCatalog(gameplay, presentation);
            return true;
        }

        // Parses compiled gameplay JSON and accepts it only when recompiling its own content
        // reproduces its revision.
        public static bool TryParseGameplay(
            string json,
            out ItemCatalogRuntimeDocument gameplay,
            out string error)
        {
            gameplay = null;
            error = string.Empty;
            try
            {
                var jsonDocument = ClientItemCatalogJson.Deserialize(json, out var revision);
                var compiled = ItemCatalogCompiler.Compile(jsonDocument.ToAuthoringDocument());
                if (!string.Equals(
                    compiled.Revision,
                    revision,
                    StringComparison.Ordinal))
                {
                    error = "The bundled item gameplay catalog revision is stale.";
                    return false;
                }

                gameplay = compiled;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public bool MatchesServerRevision(string serverRevision)
        {
            return !string.IsNullOrWhiteSpace(serverRevision)
                && string.Equals(Revision, serverRevision, StringComparison.Ordinal);
        }

        public bool TryGetDefinition(string definitionId, out ItemDefinition definition)
        {
            return definitions.TryGetValue(definitionId ?? string.Empty, out definition);
        }

        public bool TryGetEquipmentSlot(
            string equipmentSlotId,
            out ItemEquipmentSlotEntry equipmentSlot)
        {
            return equipmentSlots.TryGetValue(
                equipmentSlotId ?? string.Empty,
                out equipmentSlot);
        }

        public string GetDisplayName(string definitionId)
        {
            if (Presentation.TryGet(definitionId, out var entry)
                && !string.IsNullOrWhiteSpace(entry.fallbackDisplayName))
            {
                return entry.fallbackDisplayName;
            }

            return TryGetDefinition(definitionId, out var definition)
                ? definition.DisplayName
                : definitionId ?? string.Empty;
        }

        public string GetLocalizationKey(string definitionId)
        {
            return Presentation.TryGet(definitionId, out var entry)
                ? entry.localizationKey ?? string.Empty
                : string.Empty;
        }

        public string GetPrefabPresentationKey(string definitionId)
        {
            return Presentation.TryGet(definitionId, out var entry)
                ? entry.prefabPresentationKey ?? string.Empty
                : string.Empty;
        }

        public Sprite LoadIcon(string definitionId)
        {
            return Presentation.TryGet(definitionId ?? string.Empty, out var entry)
                ? ItemPresentationCatalogLoader.LoadIcon(entry.iconAddress)
                : null;
        }
    }

    // Only the revision of a compiled runtime catalog; the rest uses the shared catalog
    // JSON shape.
    [Serializable]
    internal sealed class ClientItemCatalogRevisionJson
    {
        public string revision;
    }

    internal static class ClientItemCatalogJson
    {
        public static ItemCatalogJsonDocument Deserialize(string json, out string revision)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Item gameplay catalog JSON is empty.");
            }

            try
            {
                var document = JsonUtility.FromJson<ItemCatalogJsonDocument>(json)
                    ?? throw new InvalidDataException(
                        "Item gameplay catalog JSON has no root object.");
                foreach (var definition in document.definitions
                    ?? Array.Empty<ItemCatalogJsonDefinition>())
                {
                    definition.ClearMissingPlaceholders();
                }

                revision = JsonUtility.FromJson<ClientItemCatalogRevisionJson>(json).revision;
                return document;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Item gameplay catalog JSON is malformed: " + exception.Message,
                    exception);
            }
        }
    }
}
