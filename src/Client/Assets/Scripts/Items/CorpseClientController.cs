using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using UnityEngine;

namespace Mmorpg.Client.Items
{
    // Loot, deposits, moves inside a corpse, and Bag swaps show at once: the corpse side as
    // edits on the corpse view, the inventory side as operations in the inventory's queue,
    // which sends every item-changing operation in order.
    [DisallowMultipleComponent]
    public sealed class CorpseClientController : MonoBehaviour
    {
        // A committed view that never follows a result is refreshed after this long.
        private const float SettleTimeoutSeconds = 2f;

        private readonly List<PendingCorpseOperation> pendingOperations = new List<PendingCorpseOperation>();
        private IRealtimeCorpseChannel simulationClient;
        private IInventoryOperationQueue inventoryController;
        private bool initialized;
        private bool refreshAfterPending;

        public CorpseClientState State { get; } = new CorpseClientState();

        public void Initialize(
            IRealtimeCorpseChannel realtimeClient,
            IInventoryOperationQueue inventory)
        {
            if (realtimeClient == null)
            {
                throw new ArgumentNullException(nameof(realtimeClient));
            }

            if (initialized)
            {
                Unsubscribe();
            }

            simulationClient = realtimeClient;
            inventoryController = inventory;
            if (inventoryController != null)
            {
                inventoryController.State.PredictionsDiscarded += OnPredictionsDiscarded;
            }
            simulationClient.CorpsePresenceChunkReceived += OnPresenceChunk;
            simulationClient.CorpseInteractionCompleted += OnInteractionCompleted;
            simulationClient.CorpseViewStateChunkReceived += OnViewStateChunk;
            simulationClient.CorpseViewClosed += OnViewClosed;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        public bool TryOpen(Guid corpseId, out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error) || corpseId == Guid.Empty)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "A valid nearby corpse is required.";
                }

                return false;
            }

            if (State.ActiveView != null && State.ActiveView.CorpseId != corpseId)
            {
                error = "Close the active corpse before opening another corpse.";
                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateOpen(operationId, corpseId),
                errorMessage: "The corpse-open request could not be sent.",
                out error);
        }

        public bool TryClose(out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error) || State.ActiveView == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "No corpse view is open.";
                }

                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateClose(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision),
                "The corpse-close request could not be sent.",
                out error);
        }

        // The player's own refresh. It clears the last outcome like any player operation;
        // the controller's automatic refresh keeps it.
        public bool TryRefresh(out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error) || State.ActiveView == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "No corpse view is open.";
                }

                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateRefresh(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision),
                "The corpse-refresh request could not be sent.",
                out error);
        }

        public bool TryLoot(
            CorpseLootItem corpseItem,
            InventoryContainer destination,
            InventorySlot destinationSlot,
            int quantity,
            out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error)
                || corpseItem == null
                || destination == null
                || destinationSlot == null)
            {
                error = Fallback(error, "Current corpse and destination state are required.");
                return false;
            }

            if (!view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot))
            {
                error = "The corpse item is no longer available.";
                return false;
            }

            if (item.HasBagContents)
            {
                error = "A corpse Bag must be swapped through the occupied Bag equipment slot.";
                return false;
            }

            if (quantity <= 0 || quantity > item.Quantity)
            {
                error = "Loot quantity must be within the current stack quantity.";
                return false;
            }

            var createdId = Guid.NewGuid();
            var containerId = destination.ContainerId;
            var slotIndex = destinationSlot.SlotIndex;
            var probe = new InventoryPredictionEdit(inventoryController.State);
            if (!CorpseInventoryPrediction.TryLoot(probe, item, quantity, containerId, slotIndex, createdId, out var displaced, out error))
            {
                return false;
            }

            var remaining = quantity < item.Quantity
                ? item.WithQuantity(item.Quantity - quantity)
                : displaced == null ? null : CorpseLootItem.FromInventoryItem(displaced);
            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out string reason) =>
                        CorpseInventoryPrediction.TryLoot(edit, item, quantity, containerId, slotIndex, createdId, out _, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendLoot(state, view.CorpseId, item.ItemInstanceId, quantity, containerId, slotIndex, out operationId, out sendError),
                    new[] { item.ItemInstanceId, displaced?.ItemInstanceId ?? Guid.Empty },
                    new[] { createdId }),
                new[] { CorpseViewEdit.SetSlot(section.ContainerId, slot.SlotIndex, remaining) },
                out error);
        }

        // Equips a whole corpse item. The item now in the equipment slot is the equipped target
        // that GameService tops up or swaps into the corpse; an empty slot sends none.
        public bool TryLootToEquipment(
            CorpseLootItem corpseItem,
            InventoryEquipmentSlot equipmentSlot,
            out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error) || corpseItem == null || equipmentSlot == null)
            {
                error = Fallback(error, "Current corpse and equipment state are required.");
                return false;
            }

            if (!view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot))
            {
                error = "The corpse item is no longer available.";
                return false;
            }

            if (item.HasBagContents)
            {
                error = "A corpse Bag must be swapped through the occupied Bag equipment slot.";
                return false;
            }

            var createdId = Guid.NewGuid();
            var slotId = equipmentSlot.SlotId;
            var probe = new InventoryPredictionEdit(inventoryController.State);
            if (!CorpseInventoryPrediction.TryLootToEquipment(probe, item, slotId, createdId, out var displaced, out error))
            {
                return false;
            }

            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out string reason) =>
                        CorpseInventoryPrediction.TryLootToEquipment(edit, item, slotId, createdId, out _, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendLootToEquipment(state, view.CorpseId, item.ItemInstanceId, slotId, out operationId, out sendError),
                    new[] { item.ItemInstanceId, displaced?.ItemInstanceId ?? Guid.Empty },
                    new[] { createdId }),
                new[]
                {
                    CorpseViewEdit.SetSlot(
                        section.ContainerId,
                        slot.SlotIndex,
                        displaced == null ? null : CorpseLootItem.FromInventoryItem(displaced))
                },
                out error);
        }

        public bool TrySwapBag(CorpseLootItem corpseBag, out string error)
        {
            var view = State.ShownView;
            var playerBag = inventoryController?.State?.FullSnapshot?.EquippedBag;
            if (!CanQueue(out error)
                || corpseBag == null
                || !corpseBag.HasBagContents
                || playerBag == null)
            {
                error = Fallback(error, "Both the corpse Bag aggregate and equipped player Bag are required.");
                return false;
            }

            if (!view.TryFindItem(corpseBag.ItemInstanceId, out var bag, out var section, out var slot)
                || !string.Equals(section.SectionKind, CorpseSectionKindIds.Equipment, StringComparison.Ordinal)
                || !view.TryGetSection(CorpseSectionKindIds.Bag, out var bagSection)
                || bagSection.ContainerId != bag.BagContentsContainerId)
            {
                error = "The corpse Bag equipment state changed.";
                return false;
            }

            if (inventoryController.State.IsPredictedContainer(playerBag.Contents.ContainerId))
            {
                error = "Wait a moment until your equipped Bag is confirmed.";
                return false;
            }

            var probe = new InventoryPredictionEdit(inventoryController.State);
            if (!CorpseInventoryPrediction.TrySwapBag(probe, bag, bagSection, out error))
            {
                return false;
            }

            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out string reason) =>
                        CorpseInventoryPrediction.TrySwapBag(edit, bag, bagSection, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendSwapBag(state, view.CorpseId, bag.ItemInstanceId, out operationId, out sendError),
                    new[] { bag.ItemInstanceId, playerBag.Item.ItemInstanceId }),
                new[]
                {
                    CorpseViewEdit.SetSlot(
                        section.ContainerId,
                        slot.SlotIndex,
                        CorpseLootItem.FromInventoryItem(
                            playerBag.Item,
                            playerBag.Contents.ContainerId,
                            playerBag.Contents.Revision)),
                    CorpseViewEdit.ReplaceSection(CorpseInventoryPrediction.ToCorpseSection(playerBag.Contents))
                },
                out error);
        }

        public bool TryDeposit(
            InventoryItem item,
            CorpseLootSection destination,
            CorpseLootSlot destinationSlot,
            int quantity,
            out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error)
                || item == null
                || destination == null
                || destinationSlot == null)
            {
                error = Fallback(error, "Current inventory, corpse, and destination state are required.");
                return false;
            }

            if (!inventoryController.State.TryFindItem(item.ItemInstanceId, out var currentItem, out var sourceLocation)
                || sourceLocation.Kind != InventoryItemLocationKind.Container
                || !ItemContainerTypeIds.IsCarried(sourceLocation.ContainerType))
            {
                error = "Only items in carried inventory, Bag, or Secure Container slots can enter a corpse.";
                return false;
            }

            if (!view.TryGetSection(destination.SectionKind, out var section)
                || section.ContainerId != destination.ContainerId
                || !section.TryGetSlot(destinationSlot.SlotIndex, out var slot))
            {
                error = "The corpse destination is no longer available.";
                return false;
            }

            if (slot.Item != null && slot.Item.HasBagContents)
            {
                error = "A corpse Bag must be exchanged through the atomic Bag aggregate swap.";
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = "Deposit quantity must be within the current stack quantity.";
                return false;
            }

            var partial = quantity < currentItem.Quantity;
            var target = slot.Item;
            var merges = target != null
                && InventoryTargetAdvisor.CanMerge(
                    inventoryController.State.Catalog,
                    InventoryPredictionEdit.WithQuantity(currentItem, quantity),
                    target.ToInventoryItem(),
                    out _);
            if (target != null && !merges && partial)
            {
                error = "A partial stack can only merge into a compatible corpse stack.";
                return false;
            }

            var swappedIn = target != null && !merges ? target : null;
            var createdId = partial ? Guid.NewGuid() : currentItem.ItemInstanceId;
            var itemId = currentItem.ItemInstanceId;
            var probe = new InventoryPredictionEdit(inventoryController.State);
            if (!CorpseInventoryPrediction.TryDeposit(probe, itemId, quantity, swappedIn, out error))
            {
                return false;
            }

            var deposited = merges
                ? target.WithQuantity(target.Quantity + quantity)
                : new CorpseLootItem(createdId, currentItem.DefinitionId, quantity, currentItem.Revision, Guid.Empty, 0);
            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out string reason) =>
                        CorpseInventoryPrediction.TryDeposit(edit, itemId, quantity, swappedIn, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendDeposit(state, view.CorpseId, itemId, quantity, section.SectionKind, slot.SlotIndex, out operationId, out sendError),
                    new[] { itemId, swappedIn?.ItemInstanceId ?? Guid.Empty },
                    partial ? new[] { createdId } : null),
                new[] { CorpseViewEdit.SetSlot(section.ContainerId, slot.SlotIndex, deposited) },
                out error);
        }

        public bool TryMoveWithinCorpse(
            CorpseLootItem item,
            CorpseLootSection destination,
            CorpseLootSlot destinationSlot,
            int quantity,
            out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error)
                || item == null
                || destination == null
                || destinationSlot == null)
            {
                error = Fallback(error, "Current corpse source and destination state are required.");
                return false;
            }

            if (!view.TryFindItem(item.ItemInstanceId, out var currentItem, out var sourceSection, out var sourceSlot)
                || !view.TryGetSection(destination.SectionKind, out var section)
                || section.ContainerId != destination.ContainerId
                || !section.TryGetSlot(destinationSlot.SlotIndex, out var slot))
            {
                error = "The corpse source or destination is no longer available.";
                return false;
            }

            if (sourceSection.ContainerId == section.ContainerId && sourceSlot.SlotIndex == slot.SlotIndex)
            {
                error = "The corpse item is already in that slot.";
                return false;
            }

            if (view.BagHasContents(currentItem) || view.BagHasContents(slot.Item))
            {
                error = "A non-empty corpse Bag cannot use an ordinary internal slot move.";
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = "Move quantity must be within the current corpse stack quantity.";
                return false;
            }

            var partial = quantity < currentItem.Quantity;
            var target = slot.Item;
            var merges = target != null
                && InventoryTargetAdvisor.CanMerge(
                    inventoryController.State.Catalog,
                    currentItem.WithQuantity(quantity).ToInventoryItem(),
                    target.ToInventoryItem(),
                    out _);
            if (target != null && !merges && partial)
            {
                error = "A partial stack can only merge into a compatible stack.";
                return false;
            }

            var createdId = partial ? Guid.NewGuid() : currentItem.ItemInstanceId;
            var itemId = currentItem.ItemInstanceId;
            var left = partial
                ? currentItem.WithQuantity(currentItem.Quantity - quantity)
                : target != null && !merges ? target : null;
            var arrived = merges
                ? target.WithQuantity(target.Quantity + quantity)
                : partial
                    ? new CorpseLootItem(createdId, currentItem.DefinitionId, quantity, 0, Guid.Empty, 0)
                    : currentItem;
            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out string reason) =>
                    {
                        reason = string.Empty;
                        return true;
                    },
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendMove(view.CorpseId, itemId, quantity, section.SectionKind, slot.SlotIndex, out operationId, out sendError),
                    new[] { itemId },
                    partial ? new[] { createdId } : null),
                new[]
                {
                    CorpseViewEdit.SetSlot(sourceSection.ContainerId, sourceSlot.SlotIndex, left),
                    CorpseViewEdit.SetSlot(section.ContainerId, slot.SlotIndex, arrived)
                },
                out error);
        }

        // A queued corpse operation and what it shows in the corpse view. After its result the
        // edits stay until a view newer than SettleAfterRevision shows the committed state.
        private sealed class PendingCorpseOperation
        {
            public PendingCorpseOperation(InventoryPredictedOperation operation, Guid corpseId, IReadOnlyList<CorpseViewEdit> edits)
            {
                Operation = operation;
                CorpseId = corpseId;
                Edits = edits;
            }

            public InventoryPredictedOperation Operation { get; }

            public Guid CorpseId { get; }

            public IReadOnlyList<CorpseViewEdit> Edits { get; }

            public long SettleAfterRevision { get; set; } = -1;

            public float SettledAt { get; set; }
        }

        private bool CanQueue(out string error)
        {
            if (!initialized || simulationClient == null || !simulationClient.IsJoined || inventoryController == null)
            {
                error = "Corpse interaction requires an active realtime shard session.";
                return false;
            }

            if (!State.CanShowOperations)
            {
                error = "No corpse view is open.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string Fallback(string error, string fallback)
        {
            return string.IsNullOrWhiteSpace(error) ? fallback : error;
        }

        private bool Queue(
            InventoryPredictedOperation operation,
            IReadOnlyList<CorpseViewEdit> edits,
            out string error)
        {
            var pending = new PendingCorpseOperation(operation, State.ActiveView.CorpseId, edits);
            pendingOperations.Add(pending);
            if (!inventoryController.TryQueueOperation(operation, out error))
            {
                pendingOperations.Remove(pending);
                return false;
            }

            PublishEdits();
            return true;
        }

        // A corpse operation waits while the corpse view has its own operation in flight, such
        // as a refresh, and fails when the corpse it was made for is gone.
        private bool TryBeginSend(Guid corpseId, out InventoryOperationSendResult result, out string error)
        {
            error = string.Empty;
            result = InventoryOperationSendResult.Sent;
            if (State.ActiveView == null || State.ActiveView.CorpseId != corpseId)
            {
                error = "The corpse is no longer open.";
                result = InventoryOperationSendResult.Failed;
                return false;
            }

            if (State.PendingOperationId != Guid.Empty)
            {
                result = InventoryOperationSendResult.Wait;
                return false;
            }

            return true;
        }

        private InventoryOperationSendResult SendIntent(
            RealtimeCorpseInteractionIntent intent,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!simulationClient.TrySendCorpseInteraction(intent))
            {
                error = "The corpse request could not be sent.";
                return InventoryOperationSendResult.Failed;
            }

            State.BeginOperation(intent.OperationId, intent.OperationKind, intent.CorpseId);
            operationId = intent.OperationId;
            error = string.Empty;
            return InventoryOperationSendResult.Sent;
        }

        private InventoryOperationSendResult SendLoot(
            InventoryClientState inventory,
            Guid corpseId,
            Guid itemInstanceId,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            if (!view.TryFindItem(itemInstanceId, out var item, out _, out _)
                || quantity > item.Quantity
                || !inventory.TryFindConfirmedContainer(destinationContainerId, out var destination)
                || !destination.TryGetSlot(destinationSlotIndex, out var slot))
            {
                error = "The corpse item or its destination changed before the loot could be sent.";
                return InventoryOperationSendResult.Failed;
            }

            var id = Guid.NewGuid();
            var targetId = slot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = slot.Item?.Revision ?? 0;
            return SendIntent(
                quantity < item.Quantity
                    ? RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision, quantity,
                        destination.ContainerId, destination.Revision, slot.SlotIndex, targetId, targetRevision)
                    : RealtimeCorpseInteractionIntent.CreateLootItem(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision,
                        destination.ContainerId, destination.Revision, slot.SlotIndex, targetId, targetRevision),
                out operationId,
                out error);
        }

        private InventoryOperationSendResult SendLootToEquipment(
            InventoryClientState inventory,
            Guid corpseId,
            Guid itemInstanceId,
            string equipmentSlotId,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            if (!view.TryFindItem(itemInstanceId, out var item, out _, out _))
            {
                error = "The corpse item changed before the loot could be sent.";
                return InventoryOperationSendResult.Failed;
            }

            var equipped = inventory.ConfirmedEquippedItem(equipmentSlotId);
            return SendIntent(
                RealtimeCorpseInteractionIntent.CreateLootToEquipment(
                    Guid.NewGuid(),
                    view.CorpseId,
                    view.Revision,
                    item.ItemInstanceId,
                    item.Revision,
                    equipmentSlotId,
                    equipped?.ItemInstanceId ?? Guid.Empty,
                    equipped?.Revision ?? 0),
                out operationId,
                out error);
        }

        private InventoryOperationSendResult SendSwapBag(
            InventoryClientState inventory,
            Guid corpseId,
            Guid corpseBagId,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            var playerBag = inventory.ConfirmedSnapshot?.EquippedBag;
            if (playerBag == null || !view.TryFindItem(corpseBagId, out var bag, out _, out _))
            {
                error = "A Bag changed before the swap could be sent.";
                return InventoryOperationSendResult.Failed;
            }

            return SendIntent(
                RealtimeCorpseInteractionIntent.CreateSwapBag(
                    Guid.NewGuid(),
                    view.CorpseId,
                    view.Revision,
                    bag.ItemInstanceId,
                    bag.Revision,
                    bag.BagContentsContainerId,
                    bag.BagContentsRevision,
                    playerBag.Item.ItemInstanceId,
                    playerBag.Item.Revision,
                    playerBag.Contents.ContainerId,
                    playerBag.Contents.Revision),
                out operationId,
                out error);
        }

        private InventoryOperationSendResult SendDeposit(
            InventoryClientState inventory,
            Guid corpseId,
            Guid itemInstanceId,
            int quantity,
            string sectionKind,
            int slotIndex,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            if (!inventory.TryFindConfirmedItem(itemInstanceId, out var item, out _)
                || quantity > item.Quantity
                || !view.TryGetSection(sectionKind, out var section)
                || !section.TryGetSlot(slotIndex, out var slot))
            {
                error = "The item or the corpse slot changed before the deposit could be sent.";
                return InventoryOperationSendResult.Failed;
            }

            var id = Guid.NewGuid();
            var targetId = slot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = slot.Item?.Revision ?? 0;
            return SendIntent(
                quantity < item.Quantity
                    ? RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision, quantity,
                        section.ContainerId, section.ContainerRevision, slot.SlotIndex, targetId, targetRevision)
                    : RealtimeCorpseInteractionIntent.CreateDepositItem(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision,
                        section.ContainerId, section.ContainerRevision, slot.SlotIndex, targetId, targetRevision),
                out operationId,
                out error);
        }

        private InventoryOperationSendResult SendMove(
            Guid corpseId,
            Guid itemInstanceId,
            int quantity,
            string sectionKind,
            int slotIndex,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            if (!view.TryFindItem(itemInstanceId, out var item, out _, out _)
                || quantity > item.Quantity
                || !view.TryGetSection(sectionKind, out var section)
                || !section.TryGetSlot(slotIndex, out var slot))
            {
                error = "The corpse changed before the move could be sent.";
                return InventoryOperationSendResult.Failed;
            }

            var id = Guid.NewGuid();
            var targetId = slot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = slot.Item?.Revision ?? 0;
            return SendIntent(
                quantity < item.Quantity
                    ? RealtimeCorpseInteractionIntent.CreateMovePartialStack(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision, quantity,
                        section.ContainerId, section.ContainerRevision, slot.SlotIndex, targetId, targetRevision)
                    : RealtimeCorpseInteractionIntent.CreateMoveItem(
                        id, view.CorpseId, view.Revision, item.ItemInstanceId, item.Revision,
                        section.ContainerId, section.ContainerRevision, slot.SlotIndex, targetId, targetRevision),
                out operationId,
                out error);
        }

        // Shows the corpse side of every operation still pending or settling on the open view.
        private void PublishEdits()
        {
            var corpseId = State.ActiveView?.CorpseId ?? Guid.Empty;
            State.SetPredictedEdits(pendingOperations
                .Where(pending => pending.CorpseId == corpseId)
                .SelectMany(pending => pending.Edits)
                .ToArray());
        }

        private void OnPredictionsDiscarded(IReadOnlyList<InventoryPredictedOperation> operations)
        {
            if (pendingOperations.RemoveAll(pending =>
                    pending.SettleAfterRevision < 0 && operations.Contains(pending.Operation)) > 0)
            {
                PublishEdits();
            }
        }

        // A view newer than the one a result left shows the committed state, so the edits of
        // the settled operation give way to it. One that never arrives is refreshed.
        private void SettleEdits()
        {
            var view = State.ActiveView;
            var removed = pendingOperations.RemoveAll(pending =>
                pending.SettleAfterRevision >= 0
                && (view == null
                    || view.CorpseId != pending.CorpseId
                    || view.Revision > pending.SettleAfterRevision));
            var expired = pendingOperations.RemoveAll(pending =>
                pending.SettleAfterRevision >= 0
                && Time.unscaledTime - pending.SettledAt > SettleTimeoutSeconds);
            if (removed + expired > 0)
            {
                PublishEdits();
            }

            if (expired > 0)
            {
                RefreshOpenView();
            }
        }

        private void Update()
        {
            if (pendingOperations.Count > 0)
            {
                SettleEdits();
            }
        }
        private bool Send(
            RealtimeCorpseInteractionIntent intent,
            string errorMessage,
            out string error)
        {
            if (!simulationClient.TrySendCorpseInteraction(intent))
            {
                error = errorMessage;
                return false;
            }

            State.BeginOperation(intent.OperationId, intent.OperationKind, intent.CorpseId);
            error = string.Empty;
            return true;
        }

        private bool CanBegin(out string error)
        {
            if (!initialized || simulationClient == null || !simulationClient.IsJoined)
            {
                error = "Corpse interaction requires an active realtime shard session.";
                return false;
            }

            if (State.PendingOperationId != Guid.Empty)
            {
                error = "Another item or corpse interaction is awaiting authority.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void OnPresenceChunk(RealtimeCorpsePresenceSnapshotChunk chunk)
        {
            var result = State.ApplyPresenceChunk(chunk);
            if (result == CorpseStateApplyResult.Invalid)
            {
                simulationClient.DisconnectForClientFailure(
                    "invalid_corpse_presence_state",
                    "Corpse presence chunks could not be assembled safely.");
            }
        }

        private void OnViewStateChunk(RealtimeCorpseViewStateChunk chunk)
        {
            var result = State.ApplyViewChunk(chunk);
            if (result == CorpseStateApplyResult.Invalid)
            {
                simulationClient.DisconnectForClientFailure(
                    "invalid_corpse_view_state",
                    "Corpse view chunks could not be assembled safely.");
                return;
            }

            SettleEdits();
            if (result == CorpseStateApplyResult.Stale)
            {
                if (State.PendingOperationId == Guid.Empty)
                {
                    RefreshOpenView();
                }
                else
                {
                    refreshAfterPending = true;
                }
            }
        }

        private void OnInteractionCompleted(RealtimeCorpseInteractionResult result)
        {
            var completesAutomaticRefresh = State.IsAutomaticRefreshPending;
            if (!State.TryComplete(result, out var error))
            {
                simulationClient.DisconnectForClientFailure(
                    "corpse_operation_result_mismatch",
                    error);
                return;
            }

            var pending = pendingOperations.FirstOrDefault(entry =>
                entry.Operation.IsSent && entry.Operation.OperationId == result.OperationId);
            if (pending != null)
            {
                // The inventory queue settles the inventory side, and the corpse side shows
                // until the view that follows the result arrives.
                if (result.Succeeded && State.ActiveView != null)
                {
                    pending.SettleAfterRevision = State.ActiveView.Revision;
                    pending.SettledAt = Time.unscaledTime;
                }
                else
                {
                    pendingOperations.Remove(pending);
                }

                PublishEdits();
                inventoryController.CompleteExternalOperation(
                    result.OperationId,
                    result.Succeeded,
                    result.Succeeded
                        ? null
                        : new InventoryClientError(result.Error.Code, result.Error.Message),
                    result.RequiresInventoryRefresh,
                    result.CarryState.ItemStateRevision);
            }
            else if (result.RequiresInventoryRefresh)
            {
                // A result that arrives after the worker closed its view still reports committed
                // custody changes.
                inventoryController?.EnsureFullState(result.CarryState.ItemStateRevision);
            }

            // A view whose delta went stale while the operation was pending needs a refresh
            // whatever the result says, and a rejection may ask for one itself. A rejected
            // automatic refresh does not ask again, so an unavailable GameService cannot
            // start a refresh loop; the player can still refresh by hand.
            if (refreshAfterPending
                || (result.RequiresCorpseRefresh && !completesAutomaticRefresh))
            {
                RefreshOpenView();
            }
        }

        // Refreshes the open view on the controller's own initiative, which keeps the
        // outcome the player last saw. A closed view is never refreshed, so a stale delta it
        // left behind is dropped.
        private void RefreshOpenView()
        {
            refreshAfterPending = false;
            if (State.ActiveView == null || !CanBegin(out _))
            {
                return;
            }

            var intent = RealtimeCorpseInteractionIntent.CreateRefresh(
                Guid.NewGuid(),
                State.ActiveView.CorpseId,
                State.ActiveView.Revision);
            if (simulationClient.TrySendCorpseInteraction(intent))
            {
                State.BeginAutomaticRefresh(intent.OperationId, intent.CorpseId);
            }
        }

        private void OnViewClosed(RealtimeCorpseViewClosed closed)
        {
            if (closed != null && State.ApplyClosure(closed.CorpseId, closed.Code, closed.Message))
            {
                refreshAfterPending = false;
                SettleEdits();
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            refreshAfterPending = false;
            pendingOperations.Clear();
            State.Reset();
            State.SetPredictedEdits(null);
        }

        private void Unsubscribe()
        {
            if (!initialized || simulationClient == null)
            {
                return;
            }

            if (inventoryController != null)
            {
                inventoryController.State.PredictionsDiscarded -= OnPredictionsDiscarded;
            }

            simulationClient.CorpsePresenceChunkReceived -= OnPresenceChunk;
            simulationClient.CorpseInteractionCompleted -= OnInteractionCompleted;
            simulationClient.CorpseViewStateChunkReceived -= OnViewStateChunk;
            simulationClient.CorpseViewClosed -= OnViewClosed;
            simulationClient.SessionEnded -= OnSessionEnded;
            initialized = false;
        }
    }
}
