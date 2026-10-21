using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Client.Items
{
    // The inventory as the server confirmed it, and as the player sees it: the confirmed state
    // with every pending predicted operation applied in order. Rules and panels read the shown
    // state; intents take their expected revisions from the confirmed one.
    public sealed class InventoryClientState : IInventoryView
    {
        // Operations the player may queue ahead of the server.
        public const int MaximumPendingOperations = 8;

        private readonly List<InventoryPredictedOperation> pendingOperations =
            new List<InventoryPredictedOperation>();
        private readonly HashSet<Guid> predictedItemIds = new HashSet<Guid>();
        private readonly HashSet<Guid> predictedContainerIds = new HashSet<Guid>();
        private CharacterInventorySnapshot confirmedSnapshot;
        private CharacterInventorySnapshot predictedSnapshot;
        private InventoryContainer bankOverride;
        private RecoveryStorageInventory recoveryOverride;
        // Operations a newer confirmed state undid, announced with the next Changed.
        private InventoryPredictionRollback pendingRollback;

        public event Action Changed;

        // Raised when predicted operations are undone, after Changed shows the restored state.
        public event Action<InventoryPredictionRollback> PredictionRolledBack;

        // Raised for every pending operation that leaves without committing, by a rejection,
        // an error, a disconnect, or a newer state it no longer fits, so the controller that
        // owns it drops what it showed outside the inventory.
        public event Action<IReadOnlyList<InventoryPredictedOperation>> PredictionsDiscarded;

        public ClientItemCatalog Catalog { get; private set; }

        public Guid CharacterId { get; private set; }

        // The shown snapshot: the confirmed one with the pending predictions applied.
        public CharacterInventorySnapshot FullSnapshot
        {
            get { return predictedSnapshot ?? confirmedSnapshot; }
        }

        public CharacterInventorySnapshot ConfirmedSnapshot
        {
            get { return confirmedSnapshot; }
        }

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
            get { return predictedSnapshot?.Bank ?? ConfirmedBank; }
        }

        public InventoryContainer ConfirmedBank
        {
            get { return bankOverride ?? confirmedSnapshot?.Bank; }
        }

        public RecoveryStorageInventory RecoveryStorage
        {
            get { return predictedSnapshot?.RecoveryStorage ?? ConfirmedRecoveryStorage; }
        }

        public RecoveryStorageInventory ConfirmedRecoveryStorage
        {
            get { return recoveryOverride ?? confirmedSnapshot?.RecoveryStorage; }
        }

        public bool RequiresFullRefresh { get; private set; }

        public IReadOnlyList<InventoryPredictedOperation> PendingOperations
        {
            get { return pendingOperations; }
        }

        public bool HasPendingOperations
        {
            get { return pendingOperations.Count > 0; }
        }

        public bool HasCoherentFullSnapshot
        {
            get
            {
                return confirmedSnapshot != null
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

        // Whether an operation that waits for the server, such as a corpse loot or a vendor
        // trade, may start: the confirmed state is current and nothing is pending.
        public bool CanMutate
        {
            get
            {
                return CanShowOperations
                    && Status == InventoryClientStatus.Ready
                    && pendingOperations.Count == 0;
            }
        }

        // Whether the player may make another item operation that the client shows at once.
        public bool CanPredict
        {
            get
            {
                return CanShowOperations
                    && Status is InventoryClientStatus.Ready or InventoryClientStatus.Busy
                    && pendingOperations.Count < MaximumPendingOperations;
            }
        }

        private bool CanShowOperations
        {
            get
            {
                return Catalog != null
                    && confirmedSnapshot != null
                    && !RequiresFullRefresh;
            }
        }

        // A stack a pending split shows has no server id yet, so nothing can act on it.
        public bool IsPredictedItem(Guid itemInstanceId)
        {
            return predictedItemIds.Contains(itemInstanceId);
        }

        // The contents of a Bag a pending equip shows have no server id yet, so nothing can
        // move into them.
        public bool IsPredictedContainer(Guid containerId)
        {
            return predictedContainerIds.Contains(containerId);
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
            DiscardPredictions();
            NotifyChanged();
        }

        public void PrepareCharacter(Guid characterId)
        {
            if (CharacterId == characterId)
            {
                return;
            }

            CharacterId = characterId;
            confirmedSnapshot = null;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = -1;
            BankObservedRevision = -1;
            RecoveryObservedRevision = -1;
            RequiresFullRefresh = true;
            DiscardPredictions();
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

            if (confirmedSnapshot != null
                && snapshot.ItemStateRevision == CharacterObservedRevision)
            {
                if (!string.Equals(
                    confirmedSnapshot.CoherenceSignature(),
                    snapshot.CoherenceSignature(),
                    StringComparison.Ordinal))
                {
                    return InventorySnapshotApplyResult.Diverged;
                }

                SetReady();
                return InventorySnapshotApplyResult.Unchanged;
            }

            confirmedSnapshot = snapshot;
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

            var currentBank = ConfirmedBank;
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

            var currentRecovery = ConfirmedRecoveryStorage;
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

        // Patches the committed change of one transaction into the coherent snapshot it follows.
        // In every other case, or when the change touches state it cannot describe, the caller
        // reloads the inventory.
        public bool TryApplyItemStateChange(RealtimeOwnerItemStateChanged change)
        {
            if (change == null
                || !HasCoherentFullSnapshot
                || CharacterObservedRevision != change.PreviousItemStateRevision
                || !InventoryItemStateChangePatch.TryApply(
                    Catalog,
                    confirmedSnapshot,
                    ConfirmedBank,
                    ConfirmedRecoveryStorage,
                    change,
                    out var patched))
            {
                return false;
            }

            // The change of the sent operation confirms it, so its prediction is not applied
            // again on top of the state that already contains it.
            if (change.OperationId != Guid.Empty
                && pendingOperations.Count > 0
                && pendingOperations[0].IsSent
                && pendingOperations[0].OperationId == change.OperationId)
            {
                pendingOperations[0].CommittedRevision = change.CarryState.ItemStateRevision;
            }

            ReplaceConfirmed(patched);
            return true;
        }

        // Patches a committed quantity change that no client intent caused, such as spent
        // arrows, into the equipped ammunition stack. It applies only to the coherent
        // snapshot the change was computed from and only when the change is the single
        // transaction after it, because every item transaction advances the item-state
        // revision by one. In every other case the caller refreshes the inventory.
        public bool TryApplyEquippedQuantityChange(RealtimeOwnerItemQuantityChanged change)
        {
            if (change == null
                || change.CarryState == null
                || change.CarryState.CarriedWeight < 0
                || change.CarryState.CarryCapacity <= 0
                || !HasCoherentFullSnapshot
                || CharacterObservedRevision != change.PreviousItemStateRevision
                || change.CarryState.ItemStateRevision != change.PreviousItemStateRevision + 1)
            {
                return false;
            }

            var equipment = confirmedSnapshot.Equipment;
            var patchedEquipment = new InventoryEquipmentSlot[equipment.Count];
            var patched = false;
            for (var index = 0; index < equipment.Count; index++)
            {
                var slot = equipment[index];
                var item = slot.Item;
                if (item == null || item.ItemInstanceId != change.ItemInstanceId)
                {
                    patchedEquipment[index] = slot;
                    continue;
                }

                // Consumption only ever shrinks the equipped ammunition stack.
                if (!string.Equals(
                        slot.SlotId,
                        ItemEquipmentSlotIds.Ammunition,
                        StringComparison.Ordinal)
                    || change.Quantity >= item.Quantity
                    || (change.Quantity > 0 && change.ItemRevision <= item.Revision))
                {
                    return false;
                }

                patchedEquipment[index] = new InventoryEquipmentSlot(
                    slot.SlotId,
                    slot.SortOrder,
                    change.Quantity == 0
                        ? null
                        : new InventoryItem(
                            item.ItemInstanceId,
                            item.DefinitionId,
                            change.Quantity,
                            change.ItemRevision,
                            item.Policies.ToArray()));
                patched = true;
            }

            if (!patched)
            {
                return false;
            }

            var carry = change.CarryState;
            ReplaceConfirmed(new CharacterInventorySnapshot(
                confirmedSnapshot.CharacterId,
                confirmedSnapshot.CatalogRevision,
                carry.ItemStateRevision,
                confirmedSnapshot.PermanentInventory,
                patchedEquipment,
                confirmedSnapshot.EquippedBag,
                ConfirmedBank,
                confirmedSnapshot.SecureContainer,
                ConfirmedRecoveryStorage,
                carry.CarriedWeight,
                carry.CarryCapacity,
                InventorySnapshotMapper.CalculateLoadRatioBasisPoints(
                    carry.CarriedWeight,
                    carry.CarryCapacity),
                EncumbranceRules.CalculateMovementMultiplierBasisPoints(
                    carry.CarriedWeight,
                    carry.CarryCapacity),
                confirmedSnapshot.Currency));
            return true;
        }

        // Shows the operation at once, on top of every operation still pending.
        public bool TryPredict(InventoryPredictedOperation operation, out string error)
        {
            error = string.Empty;
            if (operation == null)
            {
                error = "The item operation is invalid.";
                return false;
            }

            if (!CanPredict)
            {
                error = pendingOperations.Count >= MaximumPendingOperations
                    ? "Too many item moves are waiting for the server."
                    : "Inventory must finish loading in an active simulation session.";
                return false;
            }

            if (!InventoryOperationPredictor.TryApply(this, operation, IsPredictedContainer, out var predicted, out error))
            {
                return false;
            }

            pendingOperations.Add(operation);
            predictedSnapshot = predicted;
            RememberPredictedIds(operation, predicted);
            NotifyChanged();
            return true;
        }
        // The first pending operation committed and the confirmed state already contains it.
        // Unless a reload is still running, the wait an unpredicted operation showed ends.
        public void CompletePendingHead(bool endWait)
        {
            if (pendingOperations.Count == 0)
            {
                return;
            }

            pendingOperations.RemoveAt(0);
            if (endWait && Status == InventoryClientStatus.Busy)
            {
                Status = InventoryClientStatus.Ready;
            }

            RebuildPrediction();
            NotifyChanged();
        }

        // Ends the wait that an unpredicted operation showed, once its committed change and
        // its result have both arrived.
        public void EndOperationWait()
        {
            if (Status == InventoryClientStatus.Busy)
            {
                Status = InventoryClientStatus.Ready;
                NotifyChanged();
            }
        }

        // The first pending operation committed at a revision the confirmed state has not
        // reached yet, so it stays shown until a reload reaches it.
        public void MarkPendingHeadCommitted(long revision)
        {
            if (pendingOperations.Count > 0)
            {
                pendingOperations[0].CommittedRevision = revision;
            }
        }

        // Undoes every pending operation, because the server rejected the first one and the
        // others were shown on top of it.
        public void RollBackPredictions(InventoryClientError error)
        {
            var rolledBack = pendingOperations.ToArray();
            DiscardPredictions();
            Error = error;
            if (Status is InventoryClientStatus.Busy or InventoryClientStatus.Ready)
            {
                Status = InventoryClientStatus.Ready;
            }

            NotifyChanged();
            if (rolledBack.Length > 0)
            {
                PredictionRolledBack?.Invoke(new InventoryPredictionRollback(error, rolledBack));
            }
        }

        public void SetError(InventoryClientError error, bool requireFullRefresh)
        {
            Error = error;
            RequiresFullRefresh |= requireFullRefresh;
            Status = error != null && error.UpdateRequired
                ? InventoryClientStatus.UpdateRequired
                : InventoryClientStatus.Error;
            DiscardPredictions();
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
            DiscardPredictions();
            NotifyChanged();
        }

        public void ClearAll()
        {
            CharacterId = Guid.Empty;
            confirmedSnapshot = null;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = -1;
            BankObservedRevision = -1;
            RecoveryObservedRevision = -1;
            RequiresFullRefresh = Catalog == null;
            DiscardPredictions();
            RestoreCatalogStatusOrUnavailable();
            NotifyChanged();
        }

        // Finds an item in the shown state.
        public bool TryFindItem(
            Guid itemInstanceId,
            out InventoryItem item,
            out InventoryItemLocation location)
        {
            return TryFindItem(FullSnapshot, Bank, RecoveryStorage, itemInstanceId, out item, out location);
        }

        // Finds an item as the server last confirmed it, whose revision an intent expects.
        public bool TryFindConfirmedItem(
            Guid itemInstanceId,
            out InventoryItem item,
            out InventoryItemLocation location)
        {
            return TryFindItem(
                confirmedSnapshot,
                ConfirmedBank,
                ConfirmedRecoveryStorage,
                itemInstanceId,
                out item,
                out location);
        }

        // A container as the server last confirmed it, whose revision an intent expects.
        public bool TryFindConfirmedContainer(Guid containerId, out InventoryContainer container)
        {
            container = confirmedSnapshot == null
                ? null
                : new[]
                    {
                        confirmedSnapshot.PermanentInventory,
                        confirmedSnapshot.EquippedBag?.Contents,
                        confirmedSnapshot.SecureContainer?.Contents,
                        ConfirmedBank
                    }
                    .FirstOrDefault(candidate => candidate != null && candidate.ContainerId == containerId);
            return container != null;
        }

        public InventoryItem ConfirmedEquippedItem(string equipmentSlotId)
        {
            return confirmedSnapshot?.Equipment.FirstOrDefault(slot =>
                string.Equals(slot.SlotId, equipmentSlotId, StringComparison.Ordinal))?.Item;
        }

        // The confirmed state as a view that operation rules can judge.
        public IInventoryView ConfirmedView()
        {
            return confirmedSnapshot == null
                ? null
                : new InventorySnapshotView(Catalog, WithEffectiveStorage(confirmedSnapshot));
        }

        private static bool TryFindItem(
            CharacterInventorySnapshot snapshot,
            InventoryContainer bank,
            RecoveryStorageInventory recoveryStorage,
            Guid itemInstanceId,
            out InventoryItem item,
            out InventoryItemLocation location)
        {
            item = null;
            location = null;
            if (snapshot == null)
            {
                return false;
            }

            foreach (var equipmentSlot in snapshot.Equipment)
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
                snapshot.PermanentInventory,
                bank,
                snapshot.SecureContainer.Contents
            };
            if (snapshot.EquippedBag != null)
            {
                containers.Add(snapshot.EquippedBag.Contents);
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

            if (recoveryStorage == null)
            {
                return false;
            }

            foreach (var delivery in recoveryStorage.Deliveries)
            {
                foreach (var deliveryItem in delivery.Items)
                {
                    if (deliveryItem.Item.ItemInstanceId == itemInstanceId)
                    {
                        item = deliveryItem.Item;
                        location = new InventoryItemLocation(
                            InventoryItemLocationKind.RecoveryStorage,
                            recoveryStorage.ContainerId,
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

        private void ReplaceConfirmed(CharacterInventorySnapshot snapshot)
        {
            confirmedSnapshot = snapshot;
            bankOverride = null;
            recoveryOverride = null;
            CharacterObservedRevision = snapshot.ItemStateRevision;
            BankObservedRevision = snapshot.ItemStateRevision;
            RecoveryObservedRevision = snapshot.ItemStateRevision;
            RebuildPrediction();
            NotifyChanged();
        }

        // Applies the pending operations again on the newest confirmed state. One that no
        // longer applies is undone with every operation after it; one already sent stays
        // pending without a prediction, because the server still answers it.
        private void RebuildPrediction()
        {
            predictedSnapshot = null;
            predictedItemIds.Clear();
            predictedContainerIds.Clear();
            pendingOperations.RemoveAll(operation =>
                operation.CommittedRevision >= 0
                && operation.CommittedRevision <= CharacterObservedRevision);
            if (pendingOperations.Count == 0 || confirmedSnapshot == null || Catalog == null)
            {
                return;
            }

            var view = new InventorySnapshotView(Catalog, WithEffectiveStorage(confirmedSnapshot));
            var changed = false;
            for (var index = 0; index < pendingOperations.Count; index++)
            {
                var operation = pendingOperations[index];
                if (!operation.IsPredicted)
                {
                    continue;
                }

                if (!InventoryOperationPredictor.TryApply(view, operation, IsPredictedContainer, out var next, out var reason))
                {
                    var dropFrom = operation.IsSent ? index + 1 : index;
                    if (operation.IsSent)
                    {
                        operation.IsPredicted = false;
                    }

                    var dropped = pendingOperations.Skip(dropFrom).ToArray();
                    pendingOperations.RemoveRange(dropFrom, pendingOperations.Count - dropFrom);
                    if (dropped.Length > 0)
                    {
                        PredictionsDiscarded?.Invoke(dropped);
                    }
                    if (dropped.Length > 0)
                    {
                        pendingRollback = new InventoryPredictionRollback(
                            new InventoryClientError(
                                "item_move_superseded",
                                "Your item move no longer fits the inventory and was undone: " + reason),
                            dropped);
                    }

                    break;
                }

                view = new InventorySnapshotView(Catalog, next);
                changed = true;
                RememberPredictedIds(operation, next);
            }

            predictedSnapshot = changed ? view.FullSnapshot : null;
        }

        private CharacterInventorySnapshot WithEffectiveStorage(CharacterInventorySnapshot snapshot)
        {
            if (bankOverride == null && recoveryOverride == null)
            {
                return snapshot;
            }

            return new CharacterInventorySnapshot(
                snapshot.CharacterId,
                snapshot.CatalogRevision,
                snapshot.ItemStateRevision,
                snapshot.PermanentInventory,
                snapshot.Equipment.ToArray(),
                snapshot.EquippedBag,
                ConfirmedBank,
                snapshot.SecureContainer,
                ConfirmedRecoveryStorage,
                snapshot.CarriedWeight,
                snapshot.CarryCapacity,
                snapshot.LoadRatioBasisPoints,
                snapshot.MovementMultiplierBasisPoints,
                snapshot.Currency);
        }

        private void RememberPredictedIds(InventoryPredictedOperation operation, CharacterInventorySnapshot predicted)
        {
            predictedItemIds.UnionWith(operation.PredictedItemInstanceIds);
            if (predicted.EquippedBag?.Contents.ContainerId == operation.PredictedContainerId)
            {
                predictedContainerIds.Add(operation.PredictedContainerId);
            }
        }

        private void DiscardPredictions()
        {
            var discarded = pendingOperations.ToArray();
            pendingOperations.Clear();
            predictedItemIds.Clear();
            predictedContainerIds.Clear();
            if (discarded.Length > 0)
            {
                PredictionsDiscarded?.Invoke(discarded);
            }
            predictedSnapshot = null;
            pendingRollback = null;
        }

        private void SetReady()
        {
            Error = null;
            Status = InventoryClientStatus.Ready;
            RebuildPrediction();
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

        // A rollback is announced after Changed, so the restored state is drawn first.
        private void NotifyChanged()
        {
            var rollback = pendingRollback;
            pendingRollback = null;
            if (rollback != null)
            {
                Error = rollback.Error;
            }

            Changed?.Invoke();
            if (rollback != null)
            {
                PredictionRolledBack?.Invoke(rollback);
            }
        }
    }
}
