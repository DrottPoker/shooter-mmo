using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;
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

        private TextCatalog messageTexts = TextCatalog.Empty(TextTableFormat.SourceLocale);

        public void Initialize(
            IRealtimeCorpseChannel realtimeClient,
            IInventoryOperationQueue inventory,
            TextCatalog texts)
        {
            if (realtimeClient == null)
            {
                throw new ArgumentNullException(nameof(realtimeClient));
            }

            State.UseTexts(texts);
            messageTexts = texts;

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
                    error = Text("ui/corpse/nearby-corpse-required");
                }

                return false;
            }

            if (State.ActiveView != null && State.ActiveView.CorpseId != corpseId)
            {
                error = Text("ui/corpse/close-active-first");
                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateOpen(operationId, corpseId),
                errorMessage: Text("ui/corpse/open-unsent"),
                out error);
        }

        public bool TryClose(out string error)
        {
            error = string.Empty;
            if (!CanBegin(out error) || State.ActiveView == null)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = Text("ui/corpse/no-view-open");
                }

                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateClose(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision),
                Text("ui/corpse/close-unsent"),
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
                    error = Text("ui/corpse/no-view-open");
                }

                return false;
            }

            var operationId = Guid.NewGuid();
            return Send(
                RealtimeCorpseInteractionIntent.CreateRefresh(
                    operationId,
                    State.ActiveView.CorpseId,
                    State.ActiveView.Revision),
                Text("ui/corpse/refresh-unsent"),
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
                error = Fallback(error, Text("ui/corpse/loot-state-required"));
                return false;
            }

            if (!view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot))
            {
                error = Text("ui/corpse/item-unavailable");
                return false;
            }

            // An empty corpse Bag loots like any item; one with contents takes the Bag slot.
            if (view.BagHasContents(item))
            {
                error = Text("ui/corpse/bag-with-contents-loot");
                return false;
            }

            if (quantity <= 0 || quantity > item.Quantity)
            {
                error = Text("ui/corpse/loot-quantity-range");
                return false;
            }

            var createdId = Guid.NewGuid();
            var containerId = destination.ContainerId;
            var slotIndex = destinationSlot.SlotIndex;
            var probe = new InventoryPredictionEdit(inventoryController.State, inventoryController.State.IsPredictedContainer);
            if (!CorpseInventoryPrediction.TryLoot(probe, item, quantity, containerId, slotIndex, createdId, out var displaced, out var probeReason))
            {
                error = probeReason.Resolve(messageTexts);
                return false;
            }

            var remaining = quantity < item.Quantity
                ? item.WithQuantity(item.Quantity - quantity)
                : displaced == null ? null : CorpseLootItem.FromInventoryItem(displaced);
            var edits = new List<CorpseViewEdit> { CorpseViewEdit.SetSlot(section.ContainerId, slot.SlotIndex, remaining) };
            if (CorpseInventoryPrediction.OwnsBagSection(view, item))
            {
                edits.Add(CorpseInventoryPrediction.EmptyBagSection());
            }

            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out ClientText reason) =>
                        CorpseInventoryPrediction.TryLoot(edit, item, quantity, containerId, slotIndex, createdId, out _, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendLoot(state, view.CorpseId, item.ItemInstanceId, quantity, containerId, slotIndex, out operationId, out sendError),
                    new[] { item.ItemInstanceId, displaced?.ItemInstanceId ?? Guid.Empty },
                    new[] { createdId }),
                edits,
                out error);
        }

        // Loots everything that fits, as GameService plans it in one transaction.
        public bool TryLootAll(out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error))
            {
                return false;
            }

            var items = view.Sections.SelectMany(section => section.Slots).Where(slot => slot.Item != null).ToArray();
            if (items.Length == 0)
            {
                error = ClientMessages.Text(messageTexts, "corpse_emptied");
                return false;
            }

            // A live Mob corpse keeps no part of a stack. A durable Mob corpse could, but the
            // client cannot tell them apart, so a Mob corpse shows whole items.
            var wholeItemsOnly = WorldActorCorpseRules.IsMobCorpsePresentationKey(view.PresentationKey);
            var createdIds = items.ToDictionary(slot => slot.Item.ItemInstanceId, _ => Guid.NewGuid());
            var probe = new InventoryPredictionEdit(inventoryController.State, inventoryController.State.IsPredictedContainer);
            if (!CorpseLootAllPrediction.TryApply(probe, view, wholeItemsOnly, createdIds, out var probeReason))
            {
                error = probeReason.Resolve(messageTexts);
                return false;
            }

            var plan = CorpseLootAllPrediction.Plan(inventoryController.State, view, wholeItemsOnly);
            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out ClientText reason) =>
                        CorpseLootAllPrediction.TryApply(edit, view, wholeItemsOnly, createdIds, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendLootAll(view.CorpseId, out operationId, out sendError),
                    plan.Steps.Select(step => step.TargetItemInstanceId),
                    createdIds.Values),
                CorpseLootAllPrediction.CorpseEdits(view, plan),
                out error);
        }

        // Equips a whole corpse item. The item now in the equipment slot is the equipped target
        // that GameService tops up or swaps into the corpse; an empty slot sends none.
        public bool TryLootToEquipment(
            CorpseLootItem corpseItem,
            InventoryEquipmentSlot equipmentSlot,
            out string error)
        {
            return TryLootToEquipment(corpseItem, equipmentSlot, 0, out error);
        }

        // A quantity loots that part of the corpse stack: it tops up the equipped stack of its
        // kind or fills the empty slot, and the rest stays in the corpse.
        public bool TryLootToEquipment(
            CorpseLootItem corpseItem,
            InventoryEquipmentSlot equipmentSlot,
            int quantity,
            out string error)
        {
            var view = State.ShownView;
            if (!CanQueue(out error) || corpseItem == null || equipmentSlot == null)
            {
                error = Fallback(error, Text("ui/corpse/equip-state-required"));
                return false;
            }

            if (!view.TryFindItem(corpseItem.ItemInstanceId, out var item, out var section, out var slot))
            {
                error = Text("ui/corpse/item-unavailable");
                return false;
            }

            var partial = quantity > 0 && quantity < item.Quantity;
            if (quantity > item.Quantity || (partial && item.HasBagContents))
            {
                error = Text("ui/corpse/loot-quantity-range");
                return false;
            }

            // A corpse Bag equips with what it holds: the corpse Bag section, for the corpse's
            // equipped Bag, then becomes an empty one.
            InventoryContainer bagContents = null;
            var ownsBagSection = CorpseInventoryPrediction.OwnsBagSection(view, item);
            if (item.HasBagContents)
            {
                if (!inventoryController.State.Catalog.TryGetDefinition(item.DefinitionId, out var bagDefinition)
                    || bagDefinition.Bag == null)
                {
                    error = Text("ui/corpse/bag-definition-unavailable");
                    return false;
                }

                bagContents = CorpseInventoryPrediction.BagContents(view, item, bagDefinition);
            }

            var createdId = Guid.NewGuid();
            var slotId = equipmentSlot.SlotId;
            var loot = partial ? item.WithQuantity(quantity) : item;
            var probe = new InventoryPredictionEdit(inventoryController.State, inventoryController.State.IsPredictedContainer);
            if (!CorpseInventoryPrediction.TryLootToEquipment(probe, loot, slotId, createdId, bagContents, !partial, out var displaced, out var probeReason))
            {
                error = probeReason.Resolve(messageTexts);
                return false;
            }

            var edits = new List<CorpseViewEdit>
            {
                CorpseViewEdit.SetSlot(
                    section.ContainerId,
                    slot.SlotIndex,
                    partial
                        ? item.WithQuantity(item.Quantity - quantity)
                        : displaced == null ? null : CorpseLootItem.FromInventoryItem(displaced))
            };
            if (ownsBagSection)
            {
                edits.Add(CorpseInventoryPrediction.EmptyBagSection());
            }

            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out ClientText reason) =>
                        CorpseInventoryPrediction.TryLootToEquipment(edit, loot, slotId, createdId, bagContents, !partial, out _, out reason),
                    (InventoryClientState state, out Guid operationId, out string sendError) =>
                        SendLootToEquipment(state, view.CorpseId, item.ItemInstanceId, slotId, partial ? quantity : 0, out operationId, out sendError),
                    new[] { item.ItemInstanceId, displaced?.ItemInstanceId ?? Guid.Empty },
                    new[] { createdId }),
                edits,
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
                error = Fallback(error, Text("ui/corpse/bag-swap-state-required"));
                return false;
            }

            if (!view.TryFindItem(corpseBag.ItemInstanceId, out var bag, out var section, out var slot)
                || !string.Equals(section.SectionKind, CorpseSectionKindIds.Equipment, StringComparison.Ordinal)
                || !view.TryGetSection(CorpseSectionKindIds.Bag, out var bagSection)
                || bagSection.ContainerId != bag.BagContentsContainerId)
            {
                error = Text("ui/corpse/bag-state-changed");
                return false;
            }

            if (inventoryController.State.IsPredictedContainer(playerBag.Contents.ContainerId))
            {
                error = Text("ui/items/wait-for-bag");
                return false;
            }

            var probe = new InventoryPredictionEdit(inventoryController.State, inventoryController.State.IsPredictedContainer);
            if (!CorpseInventoryPrediction.TrySwapBag(probe, bag, bagSection, out var probeReason))
            {
                error = probeReason.Resolve(messageTexts);
                return false;
            }

            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out ClientText reason) =>
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
                error = Fallback(error, Text("ui/corpse/deposit-state-required"));
                return false;
            }

            if (!inventoryController.State.TryFindItem(item.ItemInstanceId, out var currentItem, out var sourceLocation)
                || sourceLocation.Kind != InventoryItemLocationKind.Container
                || !ItemContainerTypeIds.IsCarried(sourceLocation.ContainerType))
            {
                error = Text("ui/corpse/deposit-needs-carried-slot");
                return false;
            }

            if (!view.TryGetSection(destination.SectionKind, out var section)
                || section.ContainerId != destination.ContainerId
                || !section.TryGetSlot(destinationSlot.SlotIndex, out var slot))
            {
                error = Text("ui/corpse/destination-unavailable");
                return false;
            }

            if (slot.Item != null && slot.Item.HasBagContents)
            {
                error = Text("ui/corpse/bag-needs-swap");
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = Text("ui/corpse/deposit-quantity-range");
                return false;
            }

            var partial = quantity < currentItem.Quantity;
            var target = slot.Item;
            var merges = target != null
                && InventoryTargetAdvisor.CanMerge(
                    inventoryController.State.Catalog,
                    currentItem.WithQuantity(quantity),
                    target.ToInventoryItem(),
                    out _);
            if (target != null && !merges && partial)
            {
                error = Text("ui/corpse/partial-deposit-target");
                return false;
            }

            var swappedIn = target != null && !merges ? target : null;
            var createdId = partial ? Guid.NewGuid() : currentItem.ItemInstanceId;
            var itemId = currentItem.ItemInstanceId;
            var probe = new InventoryPredictionEdit(inventoryController.State, inventoryController.State.IsPredictedContainer);
            if (!CorpseInventoryPrediction.TryDeposit(probe, itemId, quantity, swappedIn, out var probeReason))
            {
                error = probeReason.Resolve(messageTexts);
                return false;
            }

            var deposited = merges
                ? target.WithQuantity(target.Quantity + quantity)
                : new CorpseLootItem(createdId, currentItem.DefinitionId, quantity, currentItem.Revision, Guid.Empty, 0);
            return Queue(
                InventoryPredictedOperation.External(
                    (InventoryPredictionEdit edit, out ClientText reason) =>
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
                error = Fallback(error, Text("ui/corpse/move-state-required"));
                return false;
            }

            if (!view.TryFindItem(item.ItemInstanceId, out var currentItem, out var sourceSection, out var sourceSlot)
                || !view.TryGetSection(destination.SectionKind, out var section)
                || section.ContainerId != destination.ContainerId
                || !section.TryGetSlot(destinationSlot.SlotIndex, out var slot))
            {
                error = Text("ui/corpse/move-unavailable");
                return false;
            }

            if (sourceSection.ContainerId == section.ContainerId && sourceSlot.SlotIndex == slot.SlotIndex)
            {
                error = Text("ui/corpse/already-in-slot");
                return false;
            }

            if (view.BagHasContents(currentItem) || view.BagHasContents(slot.Item))
            {
                error = Text("ui/corpse/bag-not-empty-move");
                return false;
            }

            if (quantity <= 0 || quantity > currentItem.Quantity)
            {
                error = Text("ui/corpse/move-quantity-range");
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
                error = Text("ui/corpse/partial-move-target");
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
                    (InventoryPredictionEdit edit, out ClientText reason) =>
                    {
                        reason = ClientText.None;
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
                error = Text("ui/corpse/session-required");
                return false;
            }

            if (!State.CanShowOperations)
            {
                error = Text("ui/corpse/no-view-open");
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string Fallback(string error, string fallback)
        {
            return string.IsNullOrWhiteSpace(error) ? fallback : error;
        }

        // The controller's own error, as the text the player reads.
        private string Text(string key)
        {
            return ClientText.Of(key).Resolve(messageTexts);
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
                error = Text("ui/corpse/no-longer-open");
                result = InventoryOperationSendResult.Failed;
                return false;
            }

            // The worker sends the committed corpse view after the result, so an earlier
            // operation's view must arrive before the next intent can expect its revisions.
            if (State.PendingOperationId != Guid.Empty
                || pendingOperations.Any(pending => pending.CorpseId == corpseId && pending.SettleAfterRevision >= 0))
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
                error = Text("ui/corpse/request-unsent");
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
                error = Text("ui/corpse/loot-changed");
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
            int quantity,
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
                error = Text("ui/corpse/equip-loot-changed");
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
                    equipped?.Revision ?? 0,
                    quantity),
                out operationId,
                out error);
        }

        private InventoryOperationSendResult SendLootAll(
            Guid corpseId,
            out Guid operationId,
            out string error)
        {
            operationId = Guid.Empty;
            if (!TryBeginSend(corpseId, out var result, out error))
            {
                return result;
            }

            var view = State.ActiveView;
            return SendIntent(
                RealtimeCorpseInteractionIntent.CreateLootAll(Guid.NewGuid(), view.CorpseId, view.Revision),
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
                error = Text("ui/corpse/bag-swap-changed");
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
                error = Text("ui/corpse/deposit-changed");
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
                error = Text("ui/corpse/move-changed");
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
                error = Text("ui/corpse/session-required");
                return false;
            }

            if (State.PendingOperationId != Guid.Empty)
            {
                error = Text("ui/corpse/interaction-pending");
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
                        : InventoryClientError.FromServer(result.Error, messageTexts),
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
