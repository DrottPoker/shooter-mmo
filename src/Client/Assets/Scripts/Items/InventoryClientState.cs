using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
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

        public bool HasCoherentFullSnapshotAtLeast(long minimumRevision)
        {
            return minimumRevision >= 0
                && HasCoherentFullSnapshot
                && CharacterObservedRevision >= minimumRevision;
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
                    location = InventoryItemLocation.ForEquipment(equipmentSlot.SlotId);
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
                            ItemContainerTypeIds.RecoveryStorage,
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
}
