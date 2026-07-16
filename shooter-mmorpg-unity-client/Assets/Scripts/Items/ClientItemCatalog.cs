using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShooterMmo.WorldData.Items;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEngine;

namespace ShooterMmo.Items
{
    public sealed class ClientItemCatalog
    {
        private readonly Dictionary<string, ItemDefinition> definitions;
        private readonly Dictionary<string, ItemCatalogIdentityEntry> equipmentSlots;

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

            ItemCatalogRuntimeDocument gameplay;
            try
            {
                var jsonDocument = ClientItemCatalogJson.Deserialize(gameplayAsset.text);
                var compiled = ItemCatalogCompiler.Compile(jsonDocument.ToAuthoringDocument());
                if (!string.Equals(
                    compiled.Revision,
                    jsonDocument.revision,
                    StringComparison.Ordinal))
                {
                    error = "The bundled item gameplay catalog revision is stale.";
                    return false;
                }

                gameplay = compiled;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (!ItemPresentationCatalogLoader.TryLoadOnce(
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
            out ItemCatalogIdentityEntry equipmentSlot)
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
            return Presentation.LoadIcon(definitionId);
        }
    }

    [Serializable]
    internal sealed class ClientItemCatalogJsonDocument
    {
        public int formatVersion;
        public string catalogId;
        public string revision;
        public string baseSecureContainerTierId;
        public ClientItemCatalogIdentityJson[] categories;
        public ClientItemCatalogIdentityJson[] tags;
        public ClientItemCatalogIdentityJson[] equipmentSlots;
        public ClientItemDefinitionJson[] definitions;
        public ClientSecureContainerTierJson[] secureContainerTiers;

        public ItemCatalogAuthoringDocument ToAuthoringDocument()
        {
            return new ItemCatalogAuthoringDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                BaseSecureContainerTierId = baseSecureContainerTierId,
                Categories = ConvertIdentities(categories),
                Tags = ConvertIdentities(tags),
                EquipmentSlots = ConvertIdentities(equipmentSlots),
                Definitions = (definitions ?? Array.Empty<ClientItemDefinitionJson>())
                    .Select(definition => definition.ToAuthoringEntry())
                    .ToArray(),
                SecureContainerTiers = (secureContainerTiers
                        ?? Array.Empty<ClientSecureContainerTierJson>())
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
            ClientItemCatalogIdentityJson[] values)
        {
            return (values ?? Array.Empty<ClientItemCatalogIdentityJson>())
                .Select(value => new ItemCatalogIdentityEntry
                {
                    Id = value.id,
                    DisplayName = value.displayName
                })
                .ToArray();
        }
    }

    [Serializable]
    internal sealed class ClientItemCatalogIdentityJson
    {
        public string id;
        public string displayName;
    }

    [Serializable]
    internal sealed class ClientItemLocationEligibilityJson
    {
        public bool secureContainer;
    }

    [Serializable]
    internal sealed class ClientBagSlotJson
    {
        public int index;
        public string kind;
        public string[] acceptedTags;
    }

    [Serializable]
    internal sealed class ClientBagDefinitionJson
    {
        public long carryCapacityBonus;
        public ClientBagSlotJson[] slots;
    }

    [Serializable]
    internal sealed class ClientItemDefinitionJson
    {
        public string id;
        public string displayName;
        public string category;
        public string[] tags;
        public long unitWeight;
        public int maximumStackSize;
        public string[] equipmentSlots;
        public bool playerDestroyable;
        public ClientItemLocationEligibilityJson locationEligibility;
        public string[] defaultPolicies;
        public ClientBagDefinitionJson bag;

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
                PlayerDestroyable = playerDestroyable,
                LocationEligibility = new ItemLocationEligibilityAuthoringEntry
                {
                    SecureContainer = locationEligibility != null
                        && locationEligibility.secureContainer
                },
                DefaultPolicies = defaultPolicies ?? Array.Empty<string>(),
                Bag = ToAuthoringBag()
            };
        }

        private BagDefinitionAuthoringEntry ToAuthoringBag()
        {
            if (!string.Equals(category, ItemCategoryIds.Bag, StringComparison.Ordinal))
            {
                return null;
            }

            return new BagDefinitionAuthoringEntry
            {
                CarryCapacityBonus = bag == null ? -1 : bag.carryCapacityBonus,
                Slots = (bag == null ? Array.Empty<ClientBagSlotJson>() : bag.slots
                        ?? Array.Empty<ClientBagSlotJson>())
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
    internal sealed class ClientSecureContainerTierJson
    {
        public string id;
        public string displayName;
        public int slotCapacity;
    }

    internal static class ClientItemCatalogJson
    {
        public static ClientItemCatalogJsonDocument Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Item gameplay catalog JSON is empty.");
            }

            try
            {
                return JsonUtility.FromJson<ClientItemCatalogJsonDocument>(json)
                    ?? throw new InvalidDataException(
                        "Item gameplay catalog JSON has no root object.");
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
