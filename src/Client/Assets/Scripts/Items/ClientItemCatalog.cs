using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Items
{
    public sealed class ClientItemCatalog
    {
        private readonly Dictionary<string, ItemDefinition> definitions;
        private readonly Dictionary<string, ItemEquipmentSlotEntry> equipmentSlots;

        private readonly HashSet<string> vendorIds;
        private readonly TextCatalog texts;

        private ClientItemCatalog(
            ItemCatalogRuntimeDocument gameplay,
            ItemPresentationCatalogIndex presentation,
            TextCatalog texts)
        {
            Gameplay = gameplay;
            Presentation = presentation;
            this.texts = texts;
            vendorIds = new HashSet<string>(
                (gameplay.Vendors ?? Array.Empty<VendorProfileDefinition>()).Select(vendor => vendor.Id),
                StringComparer.Ordinal);
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

        // The texts that name the items, which also hold the client's own sentences.
        public TextCatalog Texts
        {
            get { return texts; }
        }

        public ItemPresentationCatalogIndex Presentation { get; }

        public string Revision
        {
            get { return Gameplay.Revision; }
        }

        public IReadOnlyCollection<ItemDefinition> Definitions
        {
            get { return definitions.Values; }
        }

        // The texts are the client's text catalog, which names every item, slot, category,
        // vendor, and the currency.
        public static bool TryLoad(
            TextAsset gameplayAsset,
            TextCatalog texts,
            out ClientItemCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (texts == null)
            {
                error = "The item catalog needs the text catalog.";
                return false;
            }

            if (gameplayAsset == null)
            {
                error = "The bundled item gameplay catalog is missing.";
                return false;
            }

            if (!TryParseGameplay(gameplayAsset.text, out var gameplay, out error)
                || !ItemPresentationCatalogLoader.TryLoadOnce(out var presentation, out error))
            {
                return false;
            }

            catalog = new ClientItemCatalog(gameplay, presentation, texts);
            return true;
        }

        // Combines a verified gameplay catalog with any presentation index and the texts that
        // name it, such as a test catalog that has no bundled presentation. Items without a
        // presentation entry show the fallbacks.
        public static bool TryCreate(
            ItemCatalogRuntimeDocument gameplay,
            ItemPresentationCatalogIndex presentation,
            TextCatalog texts,
            out ClientItemCatalog catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (gameplay == null || presentation == null || texts == null)
            {
                error = "A gameplay catalog, a presentation, and the text catalog are required.";
                return false;
            }

            catalog = new ClientItemCatalog(gameplay, presentation, texts);
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
                var compiled = ItemCatalogCompiler.CompileCompiled(jsonDocument.ToAuthoringDocument());
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

        // Every name comes from the text catalog. An id that the gameplay catalog does not
        // know is shown as it is.
        public string GetDisplayName(string definitionId)
        {
            return TryGetDefinition(definitionId, out _)
                ? texts.Get(TextKeys.ItemName(definitionId))
                : definitionId ?? string.Empty;
        }

        public string GetEquipmentSlotName(string equipmentSlotId)
        {
            return TryGetEquipmentSlot(equipmentSlotId, out _)
                ? texts.Get(TextKeys.EquipmentSlotName(equipmentSlotId))
                : equipmentSlotId ?? string.Empty;
        }

        public string GetCategoryName(string categoryId)
        {
            return Contains(Gameplay.Categories, categoryId)
                ? texts.Get(TextKeys.ItemCategoryName(categoryId))
                : categoryId ?? string.Empty;
        }

        public string GetTagName(string tagId)
        {
            return Contains(Gameplay.Tags, tagId)
                ? texts.Get(TextKeys.ItemTagName(tagId))
                : tagId ?? string.Empty;
        }

        public string GetVendorName(string vendorId)
        {
            return vendorIds.Contains(vendorId ?? string.Empty)
                ? texts.Get(TextKeys.VendorName(vendorId))
                : vendorId ?? string.Empty;
        }

        public string CurrencyName
        {
            get { return texts.Get(TextKeys.CurrencyName()); }
        }

        private static bool Contains(ItemCatalogIdentityEntry[] entries, string id)
        {
            return entries != null
                && entries.Any(entry => entry != null && string.Equals(entry.Id, id, StringComparison.Ordinal));
        }

        // Empty for an item without a presentation entry.
        public string GetPrefabPresentationKey(string definitionId)
        {
            return Presentation.TryGet(definitionId, out var entry)
                ? entry.prefabPresentationKey ?? string.Empty
                : string.Empty;
        }

        // An item without an icon of its own, or whose icon is missing, shows the placeholder.
        public Sprite LoadIcon(string definitionId)
        {
            var icon = Presentation.TryGet(definitionId ?? string.Empty, out var entry)
                ? ItemPresentationCatalogLoader.LoadIcon(entry.iconAddress)
                : null;
            return icon != null
                ? icon
                : ItemPresentationCatalogLoader.LoadIcon(ItemPresentationCatalogLoader.PlaceholderIconAddress);
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
