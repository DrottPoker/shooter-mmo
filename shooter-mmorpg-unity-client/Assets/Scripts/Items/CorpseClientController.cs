using System;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Items
{
    [DisallowMultipleComponent]
    public sealed class CorpseClientController : MonoBehaviour
    {
        private RealtimeSimulationClient simulationClient;
        private InventoryClientController inventoryController;
        private bool initialized;
        private bool refreshAfterPending;

        public CorpseClientState State { get; } = new CorpseClientState();

        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            InventoryClientController inventory)
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
            simulationClient.CorpsePresenceChunkReceived += OnPresenceChunk;
            simulationClient.CorpseInteractionCompleted += OnInteractionCompleted;
            simulationClient.CorpseViewStateChunkReceived += OnViewStateChunk;
            simulationClient.CorpseViewClosed += OnViewClosed;
            simulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
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
            error = string.Empty;
            if (!CanBegin(out error)
                || State.ActiveView == null
                || corpseItem == null
                || destination == null
                || destinationSlot == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Current corpse and destination state are required.";
                }

                return false;
            }

            if (!State.ActiveView.TryFindItem(
                    corpseItem.ItemInstanceId,
                    out var currentItem,
                    out _,
                    out _))
            {
                error = "The corpse item is no longer available.";
                return false;
            }

            if (currentItem.HasBagContents)
            {
                error = "A corpse Bag must be swapped through the occupied Bag equipment slot.";
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = "Loot quantity must be within the current stack quantity.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var targetId = destinationSlot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = destinationSlot.Item?.Revision ?? 0;
            var intent = quantity < currentItem.Quantity
                ? RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    quantity,
                    destination.ContainerId,
                    destination.Revision,
                    destinationSlot.SlotIndex,
                    targetId,
                    targetRevision)
                : RealtimeCorpseInteractionIntent.CreateLootItem(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    destination.ContainerId,
                    destination.Revision,
                    destinationSlot.SlotIndex,
                    targetId,
                    targetRevision);
            return Send(intent, "The corpse-loot request could not be sent.", out error);
        }

        public bool TrySwapBag(CorpseLootItem corpseBag, out string error)
        {
            error = string.Empty;
            var playerBag = inventoryController?.State?.FullSnapshot?.EquippedBag;
            if (!CanBegin(out error)
                || State.ActiveView == null
                || corpseBag == null
                || !corpseBag.HasBagContents
                || playerBag == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Both the corpse Bag aggregate and equipped player Bag are required.";
                }

                return false;
            }

            if (!State.ActiveView.TryFindItem(
                    corpseBag.ItemInstanceId,
                    out var currentBag,
                    out var section,
                    out _)
                || !string.Equals(section.SectionKind, "equipment", StringComparison.Ordinal))
            {
                error = "The corpse Bag equipment state changed.";
                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateSwapBag(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentBag.ItemInstanceId,
                    currentBag.Revision,
                    currentBag.BagContentsContainerId,
                    currentBag.BagContentsRevision,
                    playerBag.Item.ItemInstanceId,
                    playerBag.Item.Revision,
                    playerBag.Contents.ContainerId,
                    playerBag.Contents.Revision),
                "The atomic Bag-swap request could not be sent.",
                out error);
        }

        public bool TryDeposit(
            InventoryItem item,
            CorpseLootSection destination,
            CorpseLootSlot destinationSlot,
            int quantity,
            out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error)
                || State.ActiveView == null
                || item == null
                || destination == null
                || destinationSlot == null
                || inventoryController?.State == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Current inventory, corpse, and destination state are required.";
                }

                return false;
            }

            if (!inventoryController.State.TryFindItem(
                    item.ItemInstanceId,
                    out var currentItem,
                    out var sourceLocation)
                || sourceLocation.Kind != InventoryItemLocationKind.Container
                || !IsCarriedContainerType(sourceLocation.ContainerType))
            {
                error = "Only items in carried inventory, Bag, or Secure Container slots can enter a corpse.";
                return false;
            }

            if (!State.ActiveView.TryGetSection(destination.SectionKind, out var currentSection)
                || currentSection.ContainerId != destination.ContainerId
                || !currentSection.TryGetSlot(destinationSlot.SlotIndex, out var currentSlot))
            {
                error = "The corpse destination is no longer available.";
                return false;
            }

            if (currentSlot.Item != null && currentSlot.Item.HasBagContents)
            {
                error = "A corpse Bag must be exchanged through the atomic Bag aggregate swap.";
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = "Deposit quantity must be within the current stack quantity.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var targetId = currentSlot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = currentSlot.Item?.Revision ?? 0;
            var intent = quantity < currentItem.Quantity
                ? RealtimeCorpseInteractionIntent.CreateDepositPartialStack(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    quantity,
                    currentSection.ContainerId,
                    currentSection.ContainerRevision,
                    currentSlot.SlotIndex,
                    targetId,
                    targetRevision)
                : RealtimeCorpseInteractionIntent.CreateDepositItem(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    currentSection.ContainerId,
                    currentSection.ContainerRevision,
                    currentSlot.SlotIndex,
                    targetId,
                    targetRevision);
            return Send(intent, "The corpse-deposit request could not be sent.", out error);
        }

        public bool TryMoveWithinCorpse(
            CorpseLootItem item,
            CorpseLootSection destination,
            CorpseLootSlot destinationSlot,
            int quantity,
            out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error)
                || State.ActiveView == null
                || item == null
                || destination == null
                || destinationSlot == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "Current corpse source and destination state are required.";
                }

                return false;
            }

            if (!State.ActiveView.TryFindItem(
                    item.ItemInstanceId,
                    out var currentItem,
                    out var sourceSection,
                    out var sourceSlot)
                || !State.ActiveView.TryGetSection(
                    destination.SectionKind,
                    out var currentSection)
                || currentSection.ContainerId != destination.ContainerId
                || !currentSection.TryGetSlot(destinationSlot.SlotIndex, out var currentSlot))
            {
                error = "The corpse source or destination is no longer available.";
                return false;
            }

            if (sourceSection.ContainerId == currentSection.ContainerId
                && sourceSlot.SlotIndex == currentSlot.SlotIndex)
            {
                error = "The corpse item is already in that slot.";
                return false;
            }

            if (State.ActiveView.BagHasContents(currentItem)
                || State.ActiveView.BagHasContents(currentSlot.Item))
            {
                error = "A non-empty corpse Bag cannot use an ordinary internal slot move.";
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = "Move quantity must be within the current corpse stack quantity.";
                return false;
            }

            var operationId = Guid.NewGuid();
            var targetId = currentSlot.Item?.ItemInstanceId ?? Guid.Empty;
            var targetRevision = currentSlot.Item?.Revision ?? 0;
            var intent = quantity < currentItem.Quantity
                ? RealtimeCorpseInteractionIntent.CreateMovePartialStack(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    quantity,
                    currentSection.ContainerId,
                    currentSection.ContainerRevision,
                    currentSlot.SlotIndex,
                    targetId,
                    targetRevision)
                : RealtimeCorpseInteractionIntent.CreateMoveItem(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision,
                    currentItem.ItemInstanceId,
                    currentItem.Revision,
                    currentSection.ContainerId,
                    currentSection.ContainerRevision,
                    currentSlot.SlotIndex,
                    targetId,
                    targetRevision);
            return Send(intent, "The internal corpse-move request could not be sent.", out error);
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

            State.BeginOperation(intent.OperationId);
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

            if (result == CorpseStateApplyResult.Stale)
            {
                if (State.PendingOperationId == Guid.Empty)
                {
                    TryRefresh(out _);
                }
                else
                {
                    refreshAfterPending = true;
                }
            }
        }

        private void OnInteractionCompleted(RealtimeCorpseInteractionResult result)
        {
            if (result == null || State.PendingOperationId != result.OperationId)
            {
                simulationClient.DisconnectForClientFailure(
                    "corpse_operation_result_mismatch",
                    "The corpse operation result did not match the pending operation.");
                return;
            }

            if (result.RequiresInventoryRefresh)
            {
                inventoryController?.EnsureFullState(result.CarryState.ItemStateRevision);
            }

            if (!result.Succeeded)
            {
                State.SetError(
                    result.OperationId,
                    new CorpseClientError(
                        result.Error?.Code ?? "corpse_operation_rejected",
                        result.Error?.Message ?? "The corpse operation was rejected."));
                if (result.RequiresCorpseRefresh && State.ActiveView != null)
                {
                    TryRefresh(out _);
                }

                return;
            }

            if (result.OperationKind == RealtimeCorpseInteractionKind.Close)
            {
                State.CloseView(string.Empty, string.Empty);
            }
            else
            {
                State.CompleteOperation(result.OperationId);
            }

            if (refreshAfterPending && State.ActiveView != null)
            {
                refreshAfterPending = false;
                TryRefresh(out _);
            }
        }

        private static bool IsCarriedContainerType(string containerType)
        {
            return string.Equals(containerType, "permanent_inventory", StringComparison.Ordinal)
                || string.Equals(containerType, "bag_contents", StringComparison.Ordinal)
                || string.Equals(containerType, "secure_container", StringComparison.Ordinal);
        }

        private void OnViewClosed(RealtimeCorpseViewClosed closed)
        {
            if (closed == null
                || (State.ActiveView != null && State.ActiveView.CorpseId != closed.CorpseId))
            {
                return;
            }

            State.CloseView(closed.Code, closed.Message);
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            refreshAfterPending = false;
            State.Reset();
        }

        private void Unsubscribe()
        {
            if (!initialized || simulationClient == null)
            {
                return;
            }

            simulationClient.CorpsePresenceChunkReceived -= OnPresenceChunk;
            simulationClient.CorpseInteractionCompleted -= OnInteractionCompleted;
            simulationClient.CorpseViewStateChunkReceived -= OnViewStateChunk;
            simulationClient.CorpseViewClosed -= OnViewClosed;
            simulationClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            initialized = false;
        }
    }
}
