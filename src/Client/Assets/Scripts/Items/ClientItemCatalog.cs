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
                var jsonDocument = ClientItemCatalogJson.Deserialize(json);
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

    [Serializable]
    internal sealed class ClientItemCatalogJsonDocument
    {
        public int formatVersion;
        public string catalogId;
        public string revision;
        public string baseSecureContainerTierId;
        public ClientItemCharacterJson character;
        public ClientItemCatalogIdentityJson[] categories;
        public ClientItemCatalogIdentityJson[] tags;
        public ClientItemEquipmentSlotJson[] equipmentSlots;
        public ClientItemDefinitionJson[] definitions;
        public ClientSecureContainerTierJson[] secureContainerTiers;
        public ClientItemRetiredJson retired;

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
                EquipmentSlots = (equipmentSlots ?? Array.Empty<ClientItemEquipmentSlotJson>())
                    .Select(slot => new ItemEquipmentSlotEntry
                    {
                        Id = slot.id,
                        DisplayName = slot.displayName,
                        MaximumQuantity = slot.maximumQuantity
                    })
                    .ToArray(),
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
    internal sealed class ClientItemCharacterJson
    {
        public long baseCarryCapacity;
        public int inventorySlotCount;
        public int bankSlotCount;
    }

    [Serializable]
    internal sealed class ClientItemEquipmentSlotJson
    {
        public string id;
        public string displayName;
        public int maximumQuantity;
    }

    [Serializable]
    internal sealed class ClientItemRetiredJson
    {
        public string[] categories;
        public string[] tags;
        public string[] equipmentSlots;
        public string[] definitions;
        public string[] secureContainerTiers;
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
        public string[] blockedEquipmentSlots;
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
                BlockedEquipmentSlots = blockedEquipmentSlots ?? Array.Empty<string>(),
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

        // JsonUtility never leaves a nested object null, and a valid Bag always has slots, so
        // a Bag without slots stands for no Bag.
        private BagDefinitionAuthoringEntry ToAuthoringBag()
        {
            if (bag == null || bag.slots == null || bag.slots.Length == 0)
            {
                return null;
            }

            return new BagDefinitionAuthoringEntry
            {
                CarryCapacityBonus = bag.carryCapacityBonus,
                Slots = bag.slots
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
