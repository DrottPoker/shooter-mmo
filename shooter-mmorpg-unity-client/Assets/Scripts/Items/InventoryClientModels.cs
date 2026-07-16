using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShooterMmo.Api;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Items
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
        CorpsePrepared,
        WorldLootPrepared
    }

    public enum InventoryItemLocationKind
    {
        Container,
        Equipment,
        RecoveryStorage
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
        public InventoryPolicy(string kind, string status)
        {
            Kind = kind;
            Status = status;
        }

        public string Kind { get; }

        public string Status { get; }
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

    public sealed class SecureContainerInventory
    {
        public SecureContainerInventory(
            string tierId,
            long entitlementRevision,
            InventoryContainer contents)
        {
            TierId = tierId;
            EntitlementRevision = entitlementRevision;
            Contents = contents;
        }

        public string TierId { get; }

        public long EntitlementRevision { get; }

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
            SecureContainerInventory secureContainer,
            RecoveryStorageInventory recoveryStorage,
            long carriedWeight,
            long carryCapacity,
            int loadRatioBasisPoints,
            bool sprintEligible,
            int movementMultiplierBasisPoints)
        {
            CharacterId = characterId;
            CatalogRevision = catalogRevision;
            ItemStateRevision = itemStateRevision;
            PermanentInventory = permanentInventory;
            Equipment = equipment ?? Array.Empty<InventoryEquipmentSlot>();
            EquippedBag = equippedBag;
            Bank = bank;
            SecureContainer = secureContainer;
            RecoveryStorage = recoveryStorage;
            CarriedWeight = carriedWeight;
            CarryCapacity = carryCapacity;
            LoadRatioBasisPoints = loadRatioBasisPoints;
            SprintEligible = sprintEligible;
            MovementMultiplierBasisPoints = movementMultiplierBasisPoints;
        }

        public Guid CharacterId { get; }

        public string CatalogRevision { get; }

        public long ItemStateRevision { get; }

        public InventoryContainer PermanentInventory { get; }

        public IReadOnlyList<InventoryEquipmentSlot> Equipment { get; }

        public EquippedBagInventory EquippedBag { get; }

        public InventoryContainer Bank { get; }

        public SecureContainerInventory SecureContainer { get; }

        public RecoveryStorageInventory RecoveryStorage { get; }

        public long CarriedWeight { get; }

        public long CarryCapacity { get; }

        public int LoadRatioBasisPoints { get; }

        public bool SprintEligible { get; }

        public int MovementMultiplierBasisPoints { get; }

        public string CoherenceSignature()
        {
            var builder = new StringBuilder();
            builder.Append(CharacterId).Append('|')
                .Append(CatalogRevision).Append('|')
                .Append(ItemStateRevision).Append('|')
                .Append(CarriedWeight).Append('|')
                .Append(CarryCapacity).Append('|')
                .Append(LoadRatioBasisPoints).Append('|')
                .Append(SprintEligible).Append('|')
                .Append(MovementMultiplierBasisPoints).Append('|')
                .Append(PermanentInventory.CoherenceSignature()).Append('|')
                .Append(Bank.CoherenceSignature()).Append('|')
                .Append(SecureContainer.Contents.CoherenceSignature()).Append('|')
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
                return string.Equals(ContainerType, "bank", StringComparison.Ordinal)
                    || Kind == InventoryItemLocationKind.RecoveryStorage;
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

    public sealed class InventoryClientState
    {
        private InventoryContainer bankOverride;
        private RecoveryStorageInventory recoveryOverride;

        public event Action Changed;

        public ClientItemCatalog Catalog { get; private set; }

        public Guid CharacterId { get; private set; }

        public CharacterInventorySnapshot FullSnapshot { get; private set; }

        public InventoryClientStatus Status { get; private set; } =
            InventoryClientStatus.Unavailable;

        public InventoryClientError Error { get; private set; }

        public long CharacterObservedRevision { get; private set; } = -1;

        public long BankObservedRevision { get; private set; } = -1;

        public long RecoveryObservedRevision { get; private set; } = -1;

        public long KnownItemStateRevision
        {
            get
            {
                return Math.Max(
                    CharacterObservedRevision,
                    Math.Max(BankObservedRevision, RecoveryObservedRevision));
            }
        }

        public InventoryContainer Bank
        {
            get { return bankOverride ?? FullSnapshot?.Bank; }
        }

        public RecoveryStorageInventory RecoveryStorage
        {
            get { return recoveryOverride ?? FullSnapshot?.RecoveryStorage; }
        }

        public bool RequiresFullRefresh { get; private set; }

        public bool HasCoherentFullSnapshot
        {
            get
            {
                return FullSnapshot != null
                    && CharacterObservedRevision == KnownItemStateRevision
                    && BankObservedRevision == KnownItemStateRevision
                    && RecoveryObservedRevision == KnownItemStateRevision
                    && !RequiresFullRefresh;
            }
        }

        public bool CanMutate
        {
            get
            {
                return Catalog != null
                    && FullSnapshot != null
                    && Status == InventoryClientStatus.Ready
                    && !RequiresFullRefresh;
            }
        }

        public void SetCatalog(ClientItemCatalog catalog)
        {
            Catalog = catalog;
            Error = null;
            if (Status == InventoryClientStatus.UpdateRequired)
            {
                Status = InventoryClientStatus.Unavailable;
            }

            NotifyChanged();
        }

        public void SetCatalogError(string code, string message, bool updateRequired)
        {
            Catalog = null;
            Error = new InventoryClientError(code, message, updateRequired: updateRequired);
            Status = updateRequired
                ? InventoryClientStatus.UpdateRequired
                : InventoryClientStatus.Error;
            RequiresFullRefresh = true;
            NotifyChanged();
        }

        public void PrepareCharacter(Guid characterId)
        {
            if (CharacterId == characterId)
            {
                return;
            }

            CharacterId = characterId;
            FullSnapshot = null;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = -1;
            BankObservedRevision = -1;
            RecoveryObservedRevision = -1;
            RequiresFullRefresh = true;
            RestoreCatalogStatusOrUnavailable();
            NotifyChanged();
        }

        public void BeginLoading(bool operationRefresh)
        {
            Error = null;
            Status = operationRefresh
                ? InventoryClientStatus.Busy
                : InventoryClientStatus.Loading;
            NotifyChanged();
        }

        public InventorySnapshotApplyResult ApplyFull(CharacterInventorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.CharacterId != CharacterId)
            {
                return InventorySnapshotApplyResult.WrongCharacter;
            }

            if (snapshot.ItemStateRevision < KnownItemStateRevision)
            {
                return InventorySnapshotApplyResult.Stale;
            }

            if (FullSnapshot != null
                && snapshot.ItemStateRevision == CharacterObservedRevision)
            {
                if (!string.Equals(
                    FullSnapshot.CoherenceSignature(),
                    snapshot.CoherenceSignature(),
                    StringComparison.Ordinal))
                {
                    return InventorySnapshotApplyResult.Diverged;
                }

                SetReady();
                return InventorySnapshotApplyResult.Unchanged;
            }

            FullSnapshot = snapshot;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = snapshot.ItemStateRevision;
            BankObservedRevision = snapshot.ItemStateRevision;
            RecoveryObservedRevision = snapshot.ItemStateRevision;
            RequiresFullRefresh = false;
            SetReady();
            return InventorySnapshotApplyResult.Applied;
        }

        public InventorySnapshotApplyResult ApplyBank(CharacterBankSnapshot snapshot)
        {
            if (snapshot == null || snapshot.CharacterId != CharacterId)
            {
                return InventorySnapshotApplyResult.WrongCharacter;
            }

            if (snapshot.ItemStateRevision < BankObservedRevision)
            {
                return InventorySnapshotApplyResult.Stale;
            }

            var currentBank = Bank;
            if (currentBank != null
                && snapshot.ItemStateRevision == BankObservedRevision)
            {
                if (!string.Equals(
                    currentBank.CoherenceSignature(),
                    snapshot.Bank.CoherenceSignature(),
                    StringComparison.Ordinal))
                {
                    return InventorySnapshotApplyResult.Diverged;
                }

                SetReady();
                return InventorySnapshotApplyResult.Unchanged;
            }

            bankOverride = snapshot.Bank;
            BankObservedRevision = snapshot.ItemStateRevision;
            RequiresFullRefresh |= CharacterObservedRevision != KnownItemStateRevision
                || RecoveryObservedRevision != KnownItemStateRevision;
            SetReady();
            return InventorySnapshotApplyResult.Applied;
        }

        public InventorySnapshotApplyResult ApplyRecovery(CharacterRecoverySnapshot snapshot)
        {
            if (snapshot == null || snapshot.CharacterId != CharacterId)
            {
                return InventorySnapshotApplyResult.WrongCharacter;
            }

            if (snapshot.ItemStateRevision < RecoveryObservedRevision)
            {
                return InventorySnapshotApplyResult.Stale;
            }

            var currentRecovery = RecoveryStorage;
            if (currentRecovery != null
                && snapshot.ItemStateRevision == RecoveryObservedRevision)
            {
                if (!string.Equals(
                    currentRecovery.CoherenceSignature(),
                    snapshot.RecoveryStorage.CoherenceSignature(),
                    StringComparison.Ordinal))
                {
                    return InventorySnapshotApplyResult.Diverged;
                }

                SetReady();
                return InventorySnapshotApplyResult.Unchanged;
            }

            recoveryOverride = snapshot.RecoveryStorage;
            RecoveryObservedRevision = snapshot.ItemStateRevision;
            RequiresFullRefresh |= CharacterObservedRevision != KnownItemStateRevision
                || BankObservedRevision != KnownItemStateRevision;
            SetReady();
            return InventorySnapshotApplyResult.Applied;
        }

        public void SetError(InventoryClientError error, bool requireFullRefresh)
        {
            Error = error;
            RequiresFullRefresh |= requireFullRefresh;
            Status = error != null && error.UpdateRequired
                ? InventoryClientStatus.UpdateRequired
                : InventoryClientStatus.Error;
            NotifyChanged();
        }

        public void SetServerRejection(InventoryClientError error)
        {
            Error = error;
            Status = InventoryClientStatus.Ready;
            NotifyChanged();
        }

        public void MarkDisconnected(bool operationWasPending)
        {
            RequiresFullRefresh = true;
            Error = operationWasPending
                ? new InventoryClientError(
                    "inventory_operation_uncertain",
                    "The connection closed before the item operation was reconciled. Refresh after reconnect.")
                : null;
            Status = InventoryClientStatus.Unavailable;
            NotifyChanged();
        }

        public void ClearAll()
        {
            CharacterId = Guid.Empty;
            FullSnapshot = null;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = -1;
            BankObservedRevision = -1;
            RecoveryObservedRevision = -1;
            RequiresFullRefresh = Catalog == null;
            RestoreCatalogStatusOrUnavailable();
            NotifyChanged();
        }

        public bool TryFindItem(
            Guid itemInstanceId,
            out InventoryItem item,
            out InventoryItemLocation location)
        {
            item = null;
            location = null;
            if (FullSnapshot == null)
            {
                return false;
            }

            foreach (var equipmentSlot in FullSnapshot.Equipment)
            {
                if (equipmentSlot.Item != null
                    && equipmentSlot.Item.ItemInstanceId == itemInstanceId)
                {
                    item = equipmentSlot.Item;
                    location = new InventoryItemLocation(
                        InventoryItemLocationKind.Equipment,
                        Guid.Empty,
                        "equipment",
                        -1,
                        equipmentSlot.SlotId,
                        Guid.Empty);
                    return true;
                }
            }

            var containers = new List<InventoryContainer>
            {
                FullSnapshot.PermanentInventory,
                Bank,
                FullSnapshot.SecureContainer.Contents
            };
            if (FullSnapshot.EquippedBag != null)
            {
                containers.Add(FullSnapshot.EquippedBag.Contents);
            }

            foreach (var container in containers.Where(value => value != null))
            {
                foreach (var slot in container.Slots)
                {
                    if (slot.Item != null && slot.Item.ItemInstanceId == itemInstanceId)
                    {
                        item = slot.Item;
                        location = new InventoryItemLocation(
                            InventoryItemLocationKind.Container,
                            container.ContainerId,
                            container.ContainerType,
                            slot.SlotIndex,
                            string.Empty,
                            Guid.Empty);
                        return true;
                    }
                }
            }

            if (RecoveryStorage == null)
            {
                return false;
            }

            foreach (var delivery in RecoveryStorage.Deliveries)
            {
                foreach (var deliveryItem in delivery.Items)
                {
                    if (deliveryItem.Item.ItemInstanceId == itemInstanceId)
                    {
                        item = deliveryItem.Item;
                        location = new InventoryItemLocation(
                            InventoryItemLocationKind.RecoveryStorage,
                            RecoveryStorage.ContainerId,
                            "recovery_storage",
                            deliveryItem.SlotIndex,
                            string.Empty,
                            delivery.DeliveryId);
                        return true;
                    }
                }
            }

            return false;
        }

        private void SetReady()
        {
            Error = null;
            Status = InventoryClientStatus.Ready;
            NotifyChanged();
        }

        private void RestoreCatalogStatusOrUnavailable()
        {
            if (Catalog == null && Error != null)
            {
                Status = Error.UpdateRequired
                    ? InventoryClientStatus.UpdateRequired
                    : InventoryClientStatus.Error;
                return;
            }

            Error = null;
            Status = InventoryClientStatus.Unavailable;
        }

        private void NotifyChanged()
        {
            Changed?.Invoke();
        }
    }

    public static class InventorySnapshotMapper
    {
        public static bool TryMap(
            CharacterInventorySnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterInventorySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapContainer(
                    response.permanentInventory,
                    catalog,
                    out var permanent,
                    out error)
                || !TryMapEquipment(response.equipment, catalog, out var equipment, out error)
                || !TryMapEquippedBag(response.equippedBag, catalog, out var bag, out error)
                || !TryMapContainer(response.bank, catalog, out var bank, out error)
                || !TryMapSecure(response.secureContainer, catalog, out var secure, out error)
                || !TryMapRecovery(response.recoveryStorage, catalog, out var recovery, out error))
            {
                return false;
            }

            if (response.carriedWeight < 0
                || response.carryCapacity <= 0
                || response.loadRatioBasisPoints < 0
                || response.movementMultiplierBasisPoints < 0)
            {
                error = "The inventory snapshot contains invalid carry state.";
                return false;
            }

            var expectedLoadRatio = CalculateLoadRatioBasisPoints(
                response.carriedWeight,
                response.carryCapacity);
            var expectedSprintEligibility = response.carriedWeight <= response.carryCapacity;
            var expectedMovementMultiplier = EncumbranceRules
                .CalculateMovementMultiplierBasisPoints(
                    response.carriedWeight,
                    response.carryCapacity);
            if (!EncumbranceRules.IsWithinHardCap(
                    response.carriedWeight,
                    response.carryCapacity)
                || response.loadRatioBasisPoints != expectedLoadRatio
                || response.sprintEligible != expectedSprintEligibility
                || response.movementMultiplierBasisPoints != expectedMovementMultiplier)
            {
                error = "The inventory snapshot contains inconsistent carry state.";
                return false;
            }

            snapshot = new CharacterInventorySnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                permanent,
                equipment,
                bag,
                bank,
                secure,
                recovery,
                response.carriedWeight,
                response.carryCapacity,
                response.loadRatioBasisPoints,
                response.sprintEligible,
                response.movementMultiplierBasisPoints);
            return true;
        }

        public static bool TryMap(
            CharacterBankSnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterBankSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapContainer(response.bank, catalog, out var bank, out error))
            {
                return false;
            }

            snapshot = new CharacterBankSnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                bank);
            return true;
        }

        public static bool TryMap(
            CharacterRecoverySnapshotResponse response,
            ClientItemCatalog catalog,
            out CharacterRecoverySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;
            if (!TryCharacterId(response?.characterId, out var characterId, out error)
                || !ValidateCatalog(response.catalogRevision, catalog, out error)
                || !ValidateItemStateRevision(response.itemStateRevision, out error)
                || !TryMapRecovery(
                    response.recoveryStorage,
                    catalog,
                    out var recovery,
                    out error))
            {
                return false;
            }

            snapshot = new CharacterRecoverySnapshot(
                characterId,
                response.catalogRevision,
                response.itemStateRevision,
                recovery);
            return true;
        }

        private static bool TryMapContainer(
            ItemContainerSnapshotResponse response,
            ClientItemCatalog catalog,
            out InventoryContainer container,
            out string error)
        {
            container = null;
            error = string.Empty;
            if (response == null
                || !Guid.TryParse(response.containerId, out var containerId)
                || containerId == Guid.Empty
                || string.IsNullOrWhiteSpace(response.containerType)
                || response.revision < 0)
            {
                error = "The inventory snapshot contains an invalid container.";
                return false;
            }

            var sourceSlots = response.slots ?? Array.Empty<ItemSlotSnapshotResponse>();
            if (sourceSlots.Any(slot => slot == null || slot.slotIndex < 0)
                || sourceSlots.Select(slot => slot.slotIndex).Distinct().Count()
                    != sourceSlots.Length)
            {
                error = "The inventory snapshot contains invalid or duplicate slots.";
                return false;
            }

            var slots = new List<InventorySlot>();
            foreach (var sourceSlot in sourceSlots.OrderBy(slot => slot.slotIndex))
            {
                if (!TryMapItem(sourceSlot.item, catalog, out var item, out error))
                {
                    return false;
                }

                slots.Add(new InventorySlot(
                    sourceSlot.slotIndex,
                    string.IsNullOrWhiteSpace(sourceSlot.slotKind)
                        ? BagSlotKindIds.General
                        : sourceSlot.slotKind,
                    sourceSlot.acceptedTags ?? Array.Empty<string>(),
                    item));
            }

            container = new InventoryContainer(
                containerId,
                response.containerType,
                response.revision,
                response.slotCapacity > 0 ? response.slotCapacity : (int?)null,
                slots.ToArray());
            return true;
        }

        private static bool TryMapEquipment(
            EquipmentSlotSnapshotResponse[] responses,
            ClientItemCatalog catalog,
            out InventoryEquipmentSlot[] equipment,
            out string error)
        {
            error = string.Empty;
            var source = responses ?? Array.Empty<EquipmentSlotSnapshotResponse>();
            if (source.Any(slot => slot == null || string.IsNullOrWhiteSpace(slot.equipmentSlotId))
                || source.Select(slot => slot.equipmentSlotId)
                    .Distinct(StringComparer.Ordinal).Count() != source.Length)
            {
                equipment = null;
                error = "The inventory snapshot contains invalid equipment slots.";
                return false;
            }

            var result = new List<InventoryEquipmentSlot>();
            foreach (var slot in source.OrderBy(value => value.sortOrder))
            {
                if (!catalog.TryGetEquipmentSlot(slot.equipmentSlotId, out _)
                    || !TryMapItem(slot.item, catalog, out var item, out error))
                {
                    equipment = null;
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = "The inventory snapshot references an unknown equipment slot.";
                    }

                    return false;
                }

                result.Add(new InventoryEquipmentSlot(
                    slot.equipmentSlotId,
                    slot.sortOrder,
                    item));
            }

            equipment = result.ToArray();
            return true;
        }

        private static bool TryMapEquippedBag(
            EquippedBagSnapshotResponse response,
            ClientItemCatalog catalog,
            out EquippedBagInventory bag,
            out string error)
        {
            bag = null;
            error = string.Empty;
            if (response == null)
            {
                return true;
            }

            if (!TryMapItem(response.item, catalog, out var item, out error))
            {
                return false;
            }

            if (item == null)
            {
                if (IsJsonNullPlaceholder(response.contents))
                {
                    return true;
                }

                error = "The equipped Bag snapshot is missing its Bag item.";
                return false;
            }

            if (!TryMapContainer(response.contents, catalog, out var contents, out error))
            {
                return false;
            }

            if (!catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || definition.Bag == null)
            {
                error = "The equipped Bag snapshot does not reference a Bag definition.";
                return false;
            }

            bag = new EquippedBagInventory(item, contents);
            return true;
        }

        private static bool TryMapSecure(
            SecureContainerSnapshotResponse response,
            ClientItemCatalog catalog,
            out SecureContainerInventory secure,
            out string error)
        {
            secure = null;
            error = string.Empty;
            if (response == null
                || string.IsNullOrWhiteSpace(response.tierId)
                || response.entitlementRevision < 0
                || !TryMapContainer(response.contents, catalog, out var contents, out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The inventory snapshot contains an invalid Secure Container.";
                }

                return false;
            }

            secure = new SecureContainerInventory(
                response.tierId,
                response.entitlementRevision,
                contents);
            return true;
        }

        private static bool TryMapRecovery(
            RecoveryStorageSnapshotResponse response,
            ClientItemCatalog catalog,
            out RecoveryStorageInventory recovery,
            out string error)
        {
            recovery = null;
            error = string.Empty;
            if (response == null
                || !Guid.TryParse(response.containerId, out var containerId)
                || containerId == Guid.Empty
                || response.revision < 0)
            {
                error = "The inventory snapshot contains invalid Recovery Storage.";
                return false;
            }

            var source = response.deliveries ?? Array.Empty<RecoveryDeliverySnapshotResponse>();
            var deliveries = new List<RecoveryDelivery>();
            var deliveryIds = new HashSet<Guid>();
            foreach (var delivery in source)
            {
                if (delivery == null
                    || !Guid.TryParse(delivery.deliveryId, out var deliveryId)
                    || deliveryId == Guid.Empty
                    || !deliveryIds.Add(deliveryId)
                    || delivery.revision < 0
                    || string.IsNullOrWhiteSpace(delivery.sourceKind))
                {
                    error = "The inventory snapshot contains an invalid Recovery delivery.";
                    return false;
                }

                var items = new List<RecoveryDeliveryItem>();
                foreach (var responseItem in delivery.items
                    ?? Array.Empty<RecoveryDeliveryItemSnapshotResponse>())
                {
                    if (responseItem == null
                        || responseItem.itemOrder < 0
                        || responseItem.containerSlotIndex < 0
                        || !TryMapItem(responseItem.item, catalog, out var item, out error)
                        || item == null)
                    {
                        if (string.IsNullOrWhiteSpace(error))
                        {
                            error = "The Recovery delivery contains an invalid item.";
                        }

                        return false;
                    }

                    items.Add(new RecoveryDeliveryItem(
                        responseItem.itemOrder,
                        responseItem.containerSlotIndex,
                        item));
                }

                deliveries.Add(new RecoveryDelivery(
                    deliveryId,
                    delivery.revision,
                    delivery.sourceKind,
                    delivery.createdAt,
                    delivery.availableAt,
                    delivery.expiresAt,
                    delivery.claimedAt,
                    items.OrderBy(item => item.ItemOrder).ToArray()));
            }

            recovery = new RecoveryStorageInventory(
                containerId,
                response.revision,
                deliveries.ToArray());
            return true;
        }

        private static bool TryMapItem(
            ItemInstanceSnapshotResponse response,
            ClientItemCatalog catalog,
            out InventoryItem item,
            out string error)
        {
            item = null;
            error = string.Empty;
            if (response == null || IsJsonNullPlaceholder(response))
            {
                return true;
            }

            if (!Guid.TryParse(response.itemInstanceId, out var itemId)
                || itemId == Guid.Empty)
            {
                error = "The inventory snapshot contains an item instance with an invalid id.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.definitionId)
                || !catalog.TryGetDefinition(response.definitionId, out _))
            {
                error = "The inventory snapshot references an unknown item definition.";
                return false;
            }

            if (response.quantity <= 0)
            {
                error = "The inventory snapshot contains a non-positive item quantity.";
                return false;
            }

            if (response.revision < 0)
            {
                error = "The inventory snapshot contains a negative item revision.";
                return false;
            }

            var policies = (response.policies ?? Array.Empty<ItemPolicySummaryResponse>())
                .Where(policy => policy != null)
                .Select(policy => new InventoryPolicy(
                    policy.policyKind ?? string.Empty,
                    policy.status ?? string.Empty))
                .ToArray();
            item = new InventoryItem(
                itemId,
                response.definitionId,
                response.quantity,
                response.revision,
                policies);
            return true;
        }

        private static bool IsJsonNullPlaceholder(ItemInstanceSnapshotResponse response)
        {
            return response != null
                && string.IsNullOrEmpty(response.itemInstanceId)
                && string.IsNullOrEmpty(response.definitionId)
                && response.quantity == 0
                && response.revision == 0
                && (response.policies == null || response.policies.Length == 0);
        }

        private static bool IsJsonNullPlaceholder(ItemContainerSnapshotResponse response)
        {
            return response == null
                || (string.IsNullOrEmpty(response.containerId)
                    && string.IsNullOrEmpty(response.containerType)
                    && response.revision == 0
                    && response.slotCapacity == 0
                    && (response.slots == null || response.slots.Length == 0));
        }

        private static bool ValidateCatalog(
            string revision,
            ClientItemCatalog catalog,
            out string error)
        {
            if (catalog == null || !catalog.MatchesServerRevision(revision))
            {
                error = "The server item catalog revision does not match this client build.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool ValidateItemStateRevision(long revision, out string error)
        {
            if (revision < 0)
            {
                error = "The inventory snapshot contains an invalid item-state revision.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryCharacterId(
            string value,
            out Guid characterId,
            out string error)
        {
            if (!Guid.TryParse(value, out characterId) || characterId == Guid.Empty)
            {
                error = "The inventory snapshot contains an invalid character id.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static int CalculateLoadRatioBasisPoints(long carriedWeight, long capacity)
        {
            var scaled = ((System.Numerics.BigInteger)carriedWeight * 10_000) / capacity;
            return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
        }
    }
}
