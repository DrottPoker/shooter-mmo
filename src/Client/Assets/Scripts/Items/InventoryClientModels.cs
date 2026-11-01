using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    public enum InventoryClientStatus
    {
        Unavailable,
        Loading,
        Ready,
        Busy,
        Error,
        UpdateRequired
    }

    public enum InventoryRefreshScope
    {
        Full,
        Bank,
        Recovery
    }

    public enum InventoryContextKind
    {
        None,
        Bank,
        RecoveryStorage,
        Corpse,
        WorldLootPrepared,
        Vendor
    }

    public enum InventoryItemLocationKind
    {
        Container,
        Equipment,
        RecoveryStorage,
        Corpse
    }

    public sealed class InventoryClientError
    {
        public InventoryClientError(
            string code,
            string message,
            string correlationId = "",
            bool updateRequired = false)
        {
            Code = string.IsNullOrWhiteSpace(code) ? "inventory_error" : code;
            Message = string.IsNullOrWhiteSpace(message)
                ? "The inventory request failed."
                : message;
            CorrelationId = correlationId ?? string.Empty;
            UpdateRequired = updateRequired;
        }

        public string Code { get; }

        public string Message { get; }

        public string CorrelationId { get; }

        public bool UpdateRequired { get; }

        public string ToDisplayMessage()
        {
            return Code + ": " + Message
                + (string.IsNullOrWhiteSpace(CorrelationId)
                    ? string.Empty
                    : " Correlation: " + CorrelationId + ".");
        }
    }

    public sealed class InventoryPolicy
    {
        public InventoryPolicy(string kind, string status, string protectionSource)
        {
            Kind = kind;
            Status = status;
            ProtectionSource = protectionSource;
        }

        public string Kind { get; }

        public string Status { get; }

        public string ProtectionSource { get; }
    }

    public sealed class InventoryItem
    {
        public InventoryItem(
            Guid itemInstanceId,
            string definitionId,
            int quantity,
            long revision,
            InventoryPolicy[] policies)
        {
            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Revision = revision;
            Policies = policies ?? Array.Empty<InventoryPolicy>();
        }

        public Guid ItemInstanceId { get; }

        public string DefinitionId { get; }

        public int Quantity { get; }

        public long Revision { get; }

        public IReadOnlyList<InventoryPolicy> Policies { get; }
    }

    public sealed class InventorySlot
    {
        public InventorySlot(
            int slotIndex,
            string slotKind,
            string[] acceptedTags,
            InventoryItem item)
        {
            SlotIndex = slotIndex;
            SlotKind = slotKind;
            AcceptedTags = acceptedTags ?? Array.Empty<string>();
            Item = item;
        }

        public int SlotIndex { get; }

        public string SlotKind { get; }

        public IReadOnlyList<string> AcceptedTags { get; }

        public InventoryItem Item { get; }
    }

    public sealed class InventoryContainer
    {
        private readonly Dictionary<int, InventorySlot> slotsByIndex;

        public InventoryContainer(
            Guid containerId,
            string containerType,
            long revision,
            int? slotCapacity,
            InventorySlot[] slots)
        {
            ContainerId = containerId;
            ContainerType = containerType;
            Revision = revision;
            SlotCapacity = slotCapacity;
            Slots = slots ?? Array.Empty<InventorySlot>();
            slotsByIndex = Slots.ToDictionary(slot => slot.SlotIndex);
        }

        public Guid ContainerId { get; }

        public string ContainerType { get; }

        public long Revision { get; }

        public int? SlotCapacity { get; }

        public IReadOnlyList<InventorySlot> Slots { get; }

        public bool TryGetSlot(int slotIndex, out InventorySlot slot)
        {
            return slotsByIndex.TryGetValue(slotIndex, out slot);
        }

        public string CoherenceSignature()
        {
            var builder = new StringBuilder();
            builder.Append(ContainerId).Append('|').Append(Revision);
            foreach (var slot in Slots)
            {
                builder.Append('|').Append(slot.SlotIndex).Append(':');
                if (slot.Item != null)
                {
                    builder.Append(slot.Item.ItemInstanceId)
                        .Append(':')
                        .Append(slot.Item.Revision)
                        .Append(':')
                        .Append(slot.Item.Quantity);
                }
            }

            return builder.ToString();
        }
    }

    public sealed class InventoryEquipmentSlot
    {
        public InventoryEquipmentSlot(string slotId, int sortOrder, InventoryItem item)
        {
            SlotId = slotId;
            SortOrder = sortOrder;
            Item = item;
        }

        public string SlotId { get; }

        public int SortOrder { get; }

        public InventoryItem Item { get; }
    }

    public sealed class EquippedBagInventory
    {
        public EquippedBagInventory(InventoryItem item, InventoryContainer contents)
        {
            Item = item;
            Contents = contents;
        }

        public InventoryItem Item { get; }

        public InventoryContainer Contents { get; }
    }

    public sealed class RecoveryDeliveryItem
    {
        public RecoveryDeliveryItem(int itemOrder, int slotIndex, InventoryItem item)
        {
            ItemOrder = itemOrder;
            SlotIndex = slotIndex;
            Item = item;
        }

        public int ItemOrder { get; }

        public int SlotIndex { get; }

        public InventoryItem Item { get; }
    }

    public sealed class RecoveryDelivery
    {
        public RecoveryDelivery(
            Guid deliveryId,
            long revision,
            string sourceKind,
            string createdAt,
            string availableAt,
            string expiresAt,
            string claimedAt,
            RecoveryDeliveryItem[] items)
        {
            DeliveryId = deliveryId;
            Revision = revision;
            SourceKind = sourceKind;
            CreatedAt = createdAt ?? string.Empty;
            AvailableAt = availableAt ?? string.Empty;
            ExpiresAt = expiresAt ?? string.Empty;
            ClaimedAt = claimedAt ?? string.Empty;
            Items = items ?? Array.Empty<RecoveryDeliveryItem>();
        }

        public Guid DeliveryId { get; }

        public long Revision { get; }

        public string SourceKind { get; }

        public string CreatedAt { get; }

        public string AvailableAt { get; }

        public string ExpiresAt { get; }

        public string ClaimedAt { get; }

        public IReadOnlyList<RecoveryDeliveryItem> Items { get; }

    }

    public sealed class RecoveryStorageInventory
    {
        public RecoveryStorageInventory(
            Guid containerId,
            long revision,
            RecoveryDelivery[] deliveries)
        {
            ContainerId = containerId;
            Revision = revision;
            Deliveries = deliveries ?? Array.Empty<RecoveryDelivery>();
        }

        public Guid ContainerId { get; }

        public long Revision { get; }

        public IReadOnlyList<RecoveryDelivery> Deliveries { get; }

        public string CoherenceSignature()
        {
            var builder = new StringBuilder();
            builder.Append(ContainerId).Append('|').Append(Revision);
            foreach (var delivery in Deliveries)
            {
                builder.Append('|').Append(delivery.DeliveryId)
                    .Append(':').Append(delivery.Revision);
                foreach (var item in delivery.Items)
                {
                    builder.Append(':').Append(item.Item.ItemInstanceId)
                        .Append(':').Append(item.Item.Revision);
                }
            }

            return builder.ToString();
        }
    }

    public sealed class CharacterInventorySnapshot
    {
        public CharacterInventorySnapshot(
            Guid characterId,
            string catalogRevision,
            long itemStateRevision,
            InventoryContainer permanentInventory,
            InventoryEquipmentSlot[] equipment,
            EquippedBagInventory equippedBag,
            InventoryContainer bank,
            RecoveryStorageInventory recoveryStorage,
            long carriedWeight,
            long carryCapacity,
            int loadRatioBasisPoints,
            int movementMultiplierBasisPoints,
            long currency = 0)
        {
            CharacterId = characterId;
            CatalogRevision = catalogRevision;
            ItemStateRevision = itemStateRevision;
            PermanentInventory = permanentInventory;
            Equipment = equipment ?? Array.Empty<InventoryEquipmentSlot>();
            EquippedBag = equippedBag;
            Bank = bank;
            RecoveryStorage = recoveryStorage;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            LoadRatioBasisPoints = loadRatioBasisPoints;
            MovementMultiplierBasisPoints = movementMultiplierBasisPoints;
            Currency = currency;
        }

        public Guid CharacterId { get; }

        public string CatalogRevision { get; }

        public long ItemStateRevision { get; }

        public InventoryContainer PermanentInventory { get; }

        public IReadOnlyList<InventoryEquipmentSlot> Equipment { get; }

        public EquippedBagInventory EquippedBag { get; }

        public InventoryContainer Bank { get; }

        public RecoveryStorageInventory RecoveryStorage { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public int LoadRatioBasisPoints { get; }

        public int MovementMultiplierBasisPoints { get; }

        // The character's balance. Every change while the character is online is an item
        // transaction, so it belongs to the item-state revision.
        public long Currency { get; }

        public string CoherenceSignature()
        {
            var builder = new StringBuilder();
            builder.Append(CharacterId).Append('|')
                .Append(CatalogRevision).Append('|')
                .Append(ItemStateRevision).Append('|')
                .Append(CarriedWeight).Append('|')
                .Append(CarryCapacity).Append('|')
                .Append(LoadRatioBasisPoints).Append('|')
                .Append(MovementMultiplierBasisPoints).Append('|')
                .Append(Currency).Append('|')
                .Append(PermanentInventory.CoherenceSignature()).Append('|')
                .Append(Bank.CoherenceSignature()).Append('|')
                .Append(RecoveryStorage.CoherenceSignature());
            if (EquippedBag != null)
            {
                builder.Append('|').Append(EquippedBag.Contents.CoherenceSignature());
            }

            foreach (var equipmentSlot in Equipment)
            {
                builder.Append('|').Append(equipmentSlot.SlotId).Append(':');
                if (equipmentSlot.Item != null)
                {
                    builder.Append(equipmentSlot.Item.ItemInstanceId)
                        .Append(':').Append(equipmentSlot.Item.Revision);
                }
            }

            return builder.ToString();
        }
    }

    public sealed class CharacterBankSnapshot
    {
        public CharacterBankSnapshot(
            Guid characterId,
            string catalogRevision,
            long itemStateRevision,
            InventoryContainer bank)
        {
            CharacterId = characterId;
            CatalogRevision = catalogRevision;
            ItemStateRevision = itemStateRevision;
            Bank = bank;
        }

        public Guid CharacterId { get; }

        public string CatalogRevision { get; }

        public long ItemStateRevision { get; }

        public InventoryContainer Bank { get; }
    }

    public sealed class CharacterRecoverySnapshot
    {
        public CharacterRecoverySnapshot(
            Guid characterId,
            string catalogRevision,
            long itemStateRevision,
            RecoveryStorageInventory recoveryStorage)
        {
            CharacterId = characterId;
            CatalogRevision = catalogRevision;
            ItemStateRevision = itemStateRevision;
            RecoveryStorage = recoveryStorage;
        }

        public Guid CharacterId { get; }

        public string CatalogRevision { get; }

        public long ItemStateRevision { get; }

        public RecoveryStorageInventory RecoveryStorage { get; }
    }

    public sealed class InventoryItemLocation
    {
        // Equipped items have no container, so their location names the equipment slot.
        public const string EquipmentContainerType = "equipment";

        public InventoryItemLocation(
            InventoryItemLocationKind kind,
            Guid containerId,
            string containerType,
            int slotIndex,
            string equipmentSlotId,
            Guid recoveryDeliveryId)
        {
            Kind = kind;
            ContainerId = containerId;
            ContainerType = containerType ?? string.Empty;
            SlotIndex = slotIndex;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            RecoveryDeliveryId = recoveryDeliveryId;
        }

        public static InventoryItemLocation ForEquipment(string equipmentSlotId)
        {
            return new InventoryItemLocation(
                InventoryItemLocationKind.Equipment,
                Guid.Empty,
                EquipmentContainerType,
                -1,
                equipmentSlotId,
                Guid.Empty);
        }

        public InventoryItemLocationKind Kind { get; }

        public Guid ContainerId { get; }

        public string ContainerType { get; }

        public int SlotIndex { get; }

        public string EquipmentSlotId { get; }

        public Guid RecoveryDeliveryId { get; }

        public bool IsExternal
        {
            get
            {
                return string.Equals(ContainerType, ItemContainerTypeIds.Bank, StringComparison.Ordinal)
                    || Kind == InventoryItemLocationKind.RecoveryStorage
                    || Kind == InventoryItemLocationKind.Corpse;
            }
        }
    }

    public enum InventorySnapshotApplyResult
    {
        Applied,
        Unchanged,
        Stale,
        Diverged,
        WrongCharacter
    }
}
