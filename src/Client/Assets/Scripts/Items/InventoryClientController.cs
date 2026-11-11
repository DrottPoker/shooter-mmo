using System;
using System.Collections;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Items
{
    // Shows every item operation at once and sends the pending operations one at a time. Each
    // intent expects the revisions of the confirmed state when it is sent, so a later operation
    // builds on the committed result of the one before it. A rejection undoes every pending
    // operation; a committed change patches the confirmed state, or a reload replaces it.
    // The queue that every item-changing operation goes through, so each one builds on the
    // committed result of the one before it. Corpse and vendor controllers use it.
    public interface IInventoryOperationQueue
    {
        InventoryClientState State { get; }

        bool TryQueueOperation(InventoryPredictedOperation operation, out string error);

        void CompleteExternalOperation(
            Guid operationId,
            bool succeeded,
            InventoryClientError error,
            bool requiresInventoryRefresh,
            long itemStateRevision);

        void EnsureFullState(long minimumRevision);

        // Reads the whole inventory again even when the state looks current, such as after an
        // action whose answer was lost and that may have moved items.
        void RefreshFullState();
    }

    [DisallowMultipleComponent]
    public sealed class InventoryClientController : MonoBehaviour, IInventoryOperationQueue
    {
        private const int MaximumRefreshAttempts = 3;
        private const float RefreshRetryDelaySeconds = 0.1f;

        private ApiClient apiClient;
        private TextCatalog messageTexts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RealtimeSimulationClient simulationClient;
        private Coroutine refreshCoroutine;
        // Set before the routine starts: its first step raises Changed synchronously, and a
        // listener that asks for state again must queue instead of starting a second routine.
        private bool refreshInProgress;
        // Advanced when the account or character changes, so a refresh that still waits for a
        // response of the previous session ends there without touching the next one's state.
        private int refreshGeneration;
        private InventoryRefreshScope? queuedRefreshScope;
        private long queuedMinimumRevision = -1;
        private bool queuedOperationRefresh;
        private bool initialized;
        private bool observedRealtimeJoin;
        private readonly InventoryServerRevisionFollower serverRevisionFollower =
            new InventoryServerRevisionFollower();
        private string observedAccountId = string.Empty;
        private string observedCharacterId = string.Empty;
        // The operation awaiting its result, which outlives its prediction when an error
        // discarded the pending operations.
        private Guid sentOperationId;
        private Guid lastCompletedOperationId;

        public InventoryClientState State { get; } = new InventoryClientState();

        public bool IsInitialized
        {
            get { return initialized; }
        }

        public void Initialize(
            ApiClient client,
            RealtimeSimulationClient realtimeClient,
            string gameplayCatalogJson,
            TextCatalog texts)
        {
            if (initialized)
            {
                return;
            }

            apiClient = client ?? throw new ArgumentNullException(nameof(client));
            simulationClient = realtimeClient
                ?? throw new ArgumentNullException(nameof(realtimeClient));
            messageTexts = texts ?? throw new ArgumentNullException(nameof(texts));
            if (ClientItemCatalog.TryLoad(gameplayCatalogJson, texts, out var catalog, out var error))
            {
                State.SetCatalog(catalog);
            }
            else
            {
                State.SetCatalogError(
                    "item_catalog_invalid",
                    ClientMessages.Text(texts, "item_catalog_invalid", error),
                    false);
            }

            simulationClient.ItemOperationCompleted += OnItemOperationCompleted;
            simulationClient.OwnerItemQuantityChanged += OnOwnerItemQuantityChanged;
            simulationClient.OwnerItemStateChanged += OnOwnerItemStateChanged;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;
            observedRealtimeJoin = simulationClient.IsJoined;
            ObserveSessionIdentity();
        }

        private void Update()
        {
            if (initialized)
            {
                var identityChanged = ObserveSessionIdentity();
                var joined = simulationClient != null && simulationClient.IsJoined;
                if (joined && (!observedRealtimeJoin || identityChanged))
                {
                    BeginRefresh(
                        InventoryRefreshScope.Full,
                        false,
                        State.KnownItemStateRevision);
                }

                observedRealtimeJoin = joined;
                // A committed change that no client intent caused, such as a death, reaches
                // the client only as a newer carry state. It is read a frame later, so an
                // arrow batch that patches its stack in the same packet needs no refresh.
                var serverRevision = joined ? simulationClient.CarryItemStateRevision : -1;
                if (serverRevisionFollower.ShouldRefresh(
                        serverRevision,
                        State.KnownItemStateRevision,
                        State.HasPendingOperations || sentOperationId != Guid.Empty,
                        refreshInProgress,
                        Time.unscaledTime))
                {
                    EnsureFullState(serverRevision);
                }

                SendNextPendingOperation();
            }
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.ItemOperationCompleted -= OnItemOperationCompleted;
                simulationClient.OwnerItemQuantityChanged -= OnOwnerItemQuantityChanged;
                simulationClient.OwnerItemStateChanged -= OnOwnerItemStateChanged;
                simulationClient.SessionEnded -= OnSessionEnded;
            }

            if (refreshCoroutine != null)
            {
                StopCoroutine(refreshCoroutine);
                refreshCoroutine = null;
            }

            refreshInProgress = false;
        }

        public void EnsureFullState()
        {
            EnsureFullState(State.KnownItemStateRevision);
        }

        public void EnsureFullState(long minimumRevision)
        {
            if (!TryGetSelectedCharacterId(out _))
            {
                State.SetError(
                    new InventoryClientError(
                        "inventory_character_unavailable",
                        Text("ui/inventory/character-required")),
                    true);
                return;
            }

            if (State.Catalog == null
                || (State.Status == InventoryClientStatus.Ready
                    && State.HasCoherentFullSnapshotAtLeast(minimumRevision)))
            {
                return;
            }

            BeginRefresh(
                InventoryRefreshScope.Full,
                false,
                Math.Max(State.KnownItemStateRevision, minimumRevision));
        }

        public void RefreshFullState()
        {
            BeginRefresh(InventoryRefreshScope.Full, false, State.KnownItemStateRevision);
        }

        public void RefreshContext(InventoryContextKind context)
        {
            switch (context)
            {
                case InventoryContextKind.Bank:
                    BeginRefresh(InventoryRefreshScope.Bank, false, -1);
                    break;
                case InventoryContextKind.RecoveryStorage:
                    BeginRefresh(InventoryRefreshScope.Recovery, false, -1);
                    break;
                default:
                    EnsureFullState();
                    break;
            }
        }

        public bool TryRelocate(
            InventoryItem item,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error)
        {
            return TryPrepareItemOperation(item, out _, out error)
                && TryQueue(
                    InventoryPredictedOperation.Relocate(
                        item.ItemInstanceId,
                        destinationContainerId,
                        destinationSlotIndex),
                    out error);
        }

        public bool TryEquip(
            InventoryItem item,
            string equipmentSlotId,
            out string error)
        {
            return TryEquip(item, equipmentSlotId, 0, out error);
        }

        // A quantity equips that part of a stack into an empty slot, such as some arrows.
        public bool TryEquip(
            InventoryItem item,
            string equipmentSlotId,
            int quantity,
            out string error)
        {
            return TryPrepareItemOperation(item, out _, out error)
                && TryQueue(InventoryPredictedOperation.Equip(item.ItemInstanceId, equipmentSlotId, quantity), out error);
        }

        public bool TryUnequip(
            InventoryItem item,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error)
        {
            return TryPrepareItemOperation(item, out _, out error)
                && TryQueue(
                    InventoryPredictedOperation.Unequip(
                        item.ItemInstanceId,
                        destinationContainerId,
                        destinationSlotIndex),
                    out error);
        }

        public bool TrySplit(
            InventoryItem item,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error)
        {
            return TryPrepareItemOperation(item, out _, out error)
                && Allows(InventoryTargetAdvisor.CanSplit(State.Catalog, item, quantity, out var reason), reason, out error)
                && TryQueue(
                    InventoryPredictedOperation.Split(
                        item.ItemInstanceId,
                        quantity,
                        destinationContainerId,
                        destinationSlotIndex),
                    out error);
        }

        public bool TryMerge(
            InventoryItem sourceItem,
            InventoryItem targetItem,
            out string error)
        {
            return TryMerge(sourceItem, targetItem, 0, out error);
        }

        // A quantity moves that part of the source stack and leaves the rest.
        public bool TryMerge(
            InventoryItem sourceItem,
            InventoryItem targetItem,
            int quantity,
            out string error)
        {
            if (!TryPrepareItemOperation(sourceItem, out var source, out error)
                || !TryPrepareItemOperation(targetItem, out var target, out error)
                || (quantity > 0 && quantity > sourceItem.Quantity)
                || !Allows(
                    InventoryTargetAdvisor.CanMerge(
                        State,
                        quantity > 0 ? sourceItem.WithQuantity(quantity) : sourceItem,
                        source,
                        targetItem,
                        target,
                        out var reason),
                    reason,
                    out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = Text("ui/inventory/merge-target-unavailable");
                }

                return false;
            }

            return TryQueue(
                InventoryPredictedOperation.Merge(sourceItem.ItemInstanceId, targetItem.ItemInstanceId, quantity),
                out error);
        }

        public bool TrySwap(
            InventoryItem firstItem,
            InventoryItem secondItem,
            out string error)
        {
            if (!TryPrepareItemOperation(firstItem, out var firstLocation, out error)
                || !TryPrepareItemOperation(secondItem, out var secondLocation, out error)
                || !Allows(
                    InventoryTargetAdvisor.CanSwap(
                        State,
                        firstItem,
                        firstLocation,
                        secondItem,
                        secondLocation,
                        out var reason),
                    reason,
                    out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = Text("ui/inventory/swap-target-unavailable");
                }

                return false;
            }

            return TryQueue(
                InventoryPredictedOperation.Swap(firstItem.ItemInstanceId, secondItem.ItemInstanceId),
                out error);
        }

        public bool TryDestroy(InventoryItem item, out string error)
        {
            return TryPrepareItemOperation(item, out _, out error)
                && Allows(InventoryTargetAdvisor.CanDestroy(State.Catalog, item, out var reason), reason, out error)
                && TryQueue(InventoryPredictedOperation.Destroy(item.ItemInstanceId), out error);
        }

        public bool TryClaimRecovery(
            RecoveryDelivery delivery,
            Guid destinationContainerId,
            out string error)
        {
            if (!CanStartOperation(out error) || delivery == null || delivery.Items.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = Text("ui/inventory/recovery-delivery-empty");
                }

                return false;
            }

            return TryQueue(
                InventoryPredictedOperation.ClaimRecovery(delivery, destinationContainerId),
                out error);
        }

        // The item must be the one the player sees, and a stack a pending split shows has no
        // server id to act on yet.
        private bool TryPrepareItemOperation(
            InventoryItem item,
            out InventoryItemLocation location,
            out string error)
        {
            location = null;
            if (!CanStartOperation(out error)
                || item == null
                || State.IsPredictedItem(item.ItemInstanceId)
                || !State.TryFindItem(item.ItemInstanceId, out var current, out location)
                || current.Revision != item.Revision)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = item != null && State.IsPredictedItem(item.ItemInstanceId)
                        ? Text("ui/inventory/wait-for-split")
                        : Text("ui/inventory/item-stale");
                }

                return false;
            }

            return true;
        }

        // A rule's answer, with its reason as the error text the player reads.
        private bool Allows(bool allowed, ClientText reason, out string error)
        {
            error = allowed ? string.Empty : reason.Resolve(messageTexts);
            return allowed;
        }

        // The controller's own error, as the text the player reads.
        private string Text(string key)
        {
            return messageTexts.Get(key);
        }

        private bool CanStartOperation(out string error)
        {
            error = string.Empty;
            if (!initialized
                || State.Catalog == null
                || !(State.CanPredict || State.CanMutate)
                || simulationClient == null
                || !simulationClient.IsJoined)
            {
                error = State.PendingOperations.Count >= InventoryClientState.MaximumPendingOperations
                    ? Text("ui/inventory/too-many-pending")
                    : Text("ui/inventory/session-required");
                return false;
            }

            return true;
        }

        // Queues an operation that another controller owns, such as a corpse loot or a vendor
        // trade, behind every pending item operation, so it builds on their committed results.
        public bool TryQueueOperation(InventoryPredictedOperation operation, out string error)
        {
            return CanStartOperation(out error) && TryQueue(operation, out error);
        }

        private bool TryQueue(InventoryPredictedOperation operation, out string error)
        {
            if (!State.TryPredict(operation, out error))
            {
                return false;
            }

            SendNextPendingOperation();
            return true;
        }

        // Sends the first pending operation once the confirmed state it builds on is current.
        private void SendNextPendingOperation()
        {
            if (!initialized
                || refreshInProgress
                || sentOperationId != Guid.Empty
                || simulationClient == null
                || !simulationClient.IsJoined
                || State.PendingOperations.Count == 0
                || State.Status != InventoryClientStatus.Ready
                || !State.HasCoherentFullSnapshot)
            {
                return;
            }

            var operation = State.PendingOperations[0];
            if (operation.IsSent)
            {
                return;
            }

            // An owner that answers Wait, such as a corpse with a refresh or an earlier view in
            // flight, is asked again on the next frame, because Update calls this every frame.
            if (operation.IsExternal)
            {
                switch (operation.SendExternal(State, out var externalOperationId, out var externalError))
                {
                    case InventoryOperationSendResult.Sent:
                        operation.OperationId = externalOperationId;
                        sentOperationId = externalOperationId;
                        break;
                    case InventoryOperationSendResult.Failed:
                        State.RollBackPredictions(new InventoryClientError("item_move_stale", externalError));
                        break;
                }

                return;
            }

            if (!TryCreateIntent(operation, out var intent, out var rejectionScope, out var error))
            {
                State.RollBackPredictions(new InventoryClientError("item_move_stale", error));
                return;
            }

            if (!simulationClient.TrySendItemOperation(intent))
            {
                State.RollBackPredictions(new InventoryClientError(
                    "item_move_unsent",
                    Text("ui/inventory/operation-unsent")));
                return;
            }

            operation.OperationId = intent.OperationId;
            operation.RejectionRefreshScope = rejectionScope;
            sentOperationId = intent.OperationId;
        }

        private bool TryCreateIntent(
            InventoryPredictedOperation operation,
            out RealtimeItemOperationIntent intent,
            out InventoryRefreshScope rejectionScope,
            out string error)
        {
            intent = null;
            rejectionScope = InventoryRefreshScope.Full;
            error = Text("ui/inventory/item-changed");
            var operationId = Guid.NewGuid();
            var revision = State.KnownItemStateRevision;
            if (operation.Kind == RealtimeItemOperationKind.ClaimRecoveryDelivery)
            {
                var delivery = State.ConfirmedRecoveryStorage?.Deliveries.FirstOrDefault(candidate =>
                    candidate.DeliveryId == operation.RecoveryDelivery.DeliveryId);
                if (delivery == null || delivery.Items.Count == 0)
                {
                    error = Text("ui/inventory/recovery-delivery-empty");
                    return false;
                }

                intent = RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                    operationId,
                    revision,
                    delivery.DeliveryId,
                    delivery.Revision,
                    operation.DestinationContainerId,
                    delivery.Items
                        .Select(item => new RealtimeItemRevisionExpectation(
                            item.Item.ItemInstanceId,
                            item.Item.Revision))
                        .ToArray());
                return true;
            }

            if (!State.TryFindConfirmedItem(operation.ItemInstanceId, out var item, out var location))
            {
                return false;
            }

            InventoryItem target = null;
            InventoryItemLocation targetLocation = null;
            if (operation.Kind is RealtimeItemOperationKind.MergeStacks or RealtimeItemOperationKind.SwapContainerItems
                && !State.TryFindConfirmedItem(operation.TargetItemInstanceId, out target, out targetLocation))
            {
                return false;
            }

            switch (operation.Kind)
            {
                case RealtimeItemOperationKind.Relocate:
                    intent = RealtimeItemOperationIntent.CreateRelocate(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        operation.DestinationContainerId,
                        operation.DestinationSlotIndex);
                    rejectionScope = InventoryRefreshScopes.ForMove(State, location, operation.DestinationContainerId);
                    return true;
                case RealtimeItemOperationKind.Unequip:
                    intent = RealtimeItemOperationIntent.CreateUnequip(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        operation.DestinationContainerId,
                        operation.DestinationSlotIndex);
                    return true;
                case RealtimeItemOperationKind.Equip:
                    intent = RealtimeItemOperationIntent.CreateEquip(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        operation.EquipmentSlotId,
                        operation.Quantity);
                    return true;
                case RealtimeItemOperationKind.SplitStack:
                    intent = RealtimeItemOperationIntent.CreateSplitStack(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        operation.Quantity,
                        operation.DestinationContainerId,
                        operation.DestinationSlotIndex);
                    rejectionScope = InventoryRefreshScopes.ForMove(State, location, operation.DestinationContainerId);
                    return true;
                case RealtimeItemOperationKind.MergeStacks:
                    intent = RealtimeItemOperationIntent.CreateMergeStacks(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        target.ItemInstanceId,
                        target.Revision,
                        operation.Quantity);
                    rejectionScope = InventoryRefreshScopes.ForLocations(State, location, targetLocation);
                    return true;
                case RealtimeItemOperationKind.SwapContainerItems:
                    intent = RealtimeItemOperationIntent.CreateSwapContainerItems(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision,
                        target.ItemInstanceId,
                        target.Revision);
                    rejectionScope = InventoryRefreshScopes.ForLocations(State, location, targetLocation);
                    return true;
                case RealtimeItemOperationKind.Destroy:
                    intent = RealtimeItemOperationIntent.CreateDestroy(
                        operationId,
                        revision,
                        item.ItemInstanceId,
                        item.Revision);
                    rejectionScope = InventoryRefreshScopes.ForLocations(State, location);
                    return true;
                default:
                    error = Text("ui/inventory/operation-unsupported");
                    return false;
            }
        }

        private void OnItemOperationCompleted(RealtimeItemOperationResult result)
        {
            var operation = State.PendingOperations.FirstOrDefault();
            Complete(
                result.OperationId,
                operation != null
                    && !operation.IsExternal
                    && operation.IsSent
                    && operation.OperationId == result.OperationId
                    && operation.Kind == result.OperationKind,
                result.Succeeded,
                result.Succeeded ? null : InventoryClientError.FromServer(result.Error, messageTexts),
                result.RequiresInventoryRefresh,
                result.CarryState.ItemStateRevision);
        }

        // The result of a corpse or vendor operation that this controller sent for the
        // controller that owns it. A rejection undoes every pending operation.
        public void CompleteExternalOperation(
            Guid operationId,
            bool succeeded,
            InventoryClientError error,
            bool requiresInventoryRefresh,
            long itemStateRevision)
        {
            var operation = State.PendingOperations.FirstOrDefault();
            Complete(
                operationId,
                operation != null
                    && operation.IsExternal
                    && operation.IsSent
                    && operation.OperationId == operationId,
                succeeded,
                succeeded
                    ? null
                    : error ?? InventoryClientError.FromServer(null, messageTexts),
                requiresInventoryRefresh,
                itemStateRevision);
        }

        private void Complete(
            Guid operationId,
            bool matchesHead,
            bool succeeded,
            InventoryClientError error,
            bool requiresInventoryRefresh,
            long itemStateRevision)
        {
            var operation = State.PendingOperations.FirstOrDefault();
            if (!matchesHead)
            {
                if (operationId == sentOperationId)
                {
                    // Its committed change already confirmed the operation, or an error discarded
                    // its prediction while the server worked on it. A reload runs only when the
                    // confirmed state has not reached the result.
                    sentOperationId = Guid.Empty;
                    lastCompletedOperationId = operationId;
                    if (!refreshInProgress)
                    {
                        State.EndOperationWait();
                    }

                    if (!succeeded)
                    {
                        State.RollBackPredictions(error);
                    }

                    EnsureFullState(itemStateRevision);
                    SendNextPendingOperation();
                    return;
                }

                if (operationId == lastCompletedOperationId)
                {
                    return;
                }

                sentOperationId = Guid.Empty;
                State.SetError(
                    new InventoryClientError(
                        "inventory_operation_result_mismatch",
                        Text("ui/inventory/result-mismatch")),
                    true);
                BeginRefresh(InventoryRefreshScope.Full, true, -1);
                return;
            }

            sentOperationId = Guid.Empty;
            lastCompletedOperationId = operationId;
            if (!succeeded)
            {
                State.RollBackPredictions(error);
                if (requiresInventoryRefresh)
                {
                    BeginRefresh(operation.RejectionRefreshScope, true, itemStateRevision);
                }

                return;
            }

            // The committed change arrived before the result and already patched the state.
            if (State.HasCoherentFullSnapshotAtLeast(itemStateRevision))
            {
                State.CompletePendingHead(!refreshInProgress);
                SendNextPendingOperation();
                return;
            }

            State.MarkPendingHeadCommitted(itemStateRevision);
            BeginRefresh(InventoryRefreshScope.Full, true, itemStateRevision);
        }
        // A committed transaction of the owner. A change the snapshot follows patches it;
        // any other change needs a reload unless a reload already reached it.
        private void OnOwnerItemStateChanged(RealtimeOwnerItemStateChanged change)
        {
            if (State.TryApplyItemStateChange(change)
                || State.HasCoherentFullSnapshotAtLeast(change.CarryState.ItemStateRevision))
            {
                return;
            }

            EnsureFullState(change.CarryState.ItemStateRevision);
        }

        // Spent arrows commit without a client intent. A snapshot at exactly the revision the
        // change applies to is patched in place; any other snapshot is refreshed.
        private void OnOwnerItemQuantityChanged(RealtimeOwnerItemQuantityChanged change)
        {
            if (!State.TryApplyEquippedQuantityChange(change))
            {
                EnsureFullState(change.CarryState.ItemStateRevision);
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            State.MarkDisconnected(State.HasPendingOperations || sentOperationId != Guid.Empty);
            sentOperationId = Guid.Empty;
            observedRealtimeJoin = false;
            serverRevisionFollower.Reset();
        }


        private void BeginRefresh(
            InventoryRefreshScope scope,
            bool operationRefresh,
            long minimumRevision)
        {
            if (!initialized || State.Catalog == null || !TryGetSelectedCharacterId(out _))
            {
                return;
            }

            if (refreshInProgress)
            {
                QueueRefresh(scope, operationRefresh, minimumRevision);
                return;
            }

            refreshInProgress = true;
            var routine = StartCoroutine(
                RefreshRoutine(scope, operationRefresh, minimumRevision, refreshGeneration));
            // A routine that finished without yielding has already cleared the flag, and a
            // queued refresh it started has stored its own handle.
            if (refreshInProgress && refreshCoroutine == null)
            {
                refreshCoroutine = routine;
            }
        }

        private IEnumerator RefreshRoutine(
            InventoryRefreshScope scope,
            bool operationRefresh,
            long minimumRevision,
            int generation)
        {
            State.BeginLoading(operationRefresh);
            var succeeded = false;
            for (var attempt = 0; attempt < MaximumRefreshAttempts; attempt++)
            {
                if (!TryGetSelectedCharacterId(out var characterId))
                {
                    break;
                }

                var token = ClientSession.SessionToken;
                ApiError apiError = null;
                InventorySnapshotApplyResult applyResult;
                long receivedRevision;

                if (scope == InventoryRefreshScope.Bank)
                {
                    CharacterBankSnapshotResponse response = null;
                    var mapError = string.Empty;
                    yield return apiClient.GetCharacterBank(
                        ClientSession.GameServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
                    if (generation != refreshGeneration)
                    {
                        yield break;
                    }

                    if (apiError == null
                        && TryMapBank(response, out var snapshot, out mapError))
                    {
                        receivedRevision = snapshot.ItemStateRevision;
                        applyResult = State.ApplyBank(snapshot);
                    }
                    else
                    {
                        if (apiError == null)
                        {
                            apiError = InvalidResponse(mapError);
                        }

                        receivedRevision = -1;
                        applyResult = InventorySnapshotApplyResult.Stale;
                    }
                }
                else if (scope == InventoryRefreshScope.Recovery)
                {
                    CharacterRecoverySnapshotResponse response = null;
                    var mapError = string.Empty;
                    yield return apiClient.GetCharacterRecovery(
                        ClientSession.GameServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
                    if (generation != refreshGeneration)
                    {
                        yield break;
                    }

                    if (apiError == null
                        && TryMapRecovery(response, out var snapshot, out mapError))
                    {
                        receivedRevision = snapshot.ItemStateRevision;
                        applyResult = State.ApplyRecovery(snapshot);
                    }
                    else
                    {
                        if (apiError == null)
                        {
                            apiError = InvalidResponse(mapError);
                        }

                        receivedRevision = -1;
                        applyResult = InventorySnapshotApplyResult.Stale;
                    }
                }
                else
                {
                    CharacterInventorySnapshotResponse response = null;
                    var mapError = string.Empty;
                    yield return apiClient.GetCharacterInventory(
                        ClientSession.GameServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
                    if (generation != refreshGeneration)
                    {
                        yield break;
                    }

                    if (apiError == null
                        && TryMapFull(response, out var snapshot, out mapError))
                    {
                        receivedRevision = snapshot.ItemStateRevision;
                        applyResult = State.ApplyFull(snapshot);
                    }
                    else
                    {
                        if (apiError == null)
                        {
                            apiError = InvalidResponse(mapError);
                        }

                        receivedRevision = -1;
                        applyResult = InventorySnapshotApplyResult.Stale;
                    }
                }

                if (apiError != null)
                {
                    if (State.Status != InventoryClientStatus.UpdateRequired
                        && !ClientSessionRecovery.ReturnToLoginIfUnauthorized(
                            apiError,
                            simulationClient))
                    {
                        State.SetError(InventoryClientError.FromApi(apiError, messageTexts), true);
                    }

                    break;
                }

                if (applyResult == InventorySnapshotApplyResult.Diverged)
                {
                    State.SetError(
                        new InventoryClientError(
                            "inventory_state_diverged",
                            Text("ui/inventory/state-diverged")),
                        true);
                    break;
                }

                if (applyResult == InventorySnapshotApplyResult.WrongCharacter)
                {
                    State.SetError(
                        new InventoryClientError(
                            "inventory_character_changed",
                            Text("ui/inventory/character-changed")),
                        true);
                    break;
                }

                if (applyResult != InventorySnapshotApplyResult.Stale
                    && (minimumRevision < 0 || receivedRevision >= minimumRevision))
                {
                    succeeded = true;
                    break;
                }

                if (attempt + 1 < MaximumRefreshAttempts)
                {
                    yield return new WaitForSecondsRealtime(RefreshRetryDelaySeconds);
                    if (generation != refreshGeneration)
                    {
                        yield break;
                    }
                }
            }

            if (!succeeded
                && (State.Status is InventoryClientStatus.Loading
                    or InventoryClientStatus.Busy))
            {
                State.SetError(
                    new InventoryClientError(
                        "inventory_refresh_stale",
                        Text("ui/inventory/refresh-stale")),
                    true);
            }

            refreshCoroutine = null;
            refreshInProgress = false;
            if (succeeded
                && scope != InventoryRefreshScope.Full
                && State.RequiresFullRefresh)
            {
                BeginRefresh(
                    InventoryRefreshScope.Full,
                    false,
                    State.KnownItemStateRevision);
                yield break;
            }

            StartQueuedRefresh();
            SendNextPendingOperation();
        }

        private bool TryMapFull(
            CharacterInventorySnapshotResponse response,
            out CharacterInventorySnapshot snapshot,
            out string error)
        {
            if (response != null
                && !State.Catalog.MatchesServerRevision(response.catalogRevision))
            {
                snapshot = null;
                error = ClientMessages.Text(messageTexts, "item_catalog_update_required");
                State.SetCatalogError("item_catalog_update_required", error, true);
                return false;
            }

            return InventorySnapshotMapper.TryMap(response, State.Catalog, out snapshot, out error);
        }

        private bool TryMapBank(
            CharacterBankSnapshotResponse response,
            out CharacterBankSnapshot snapshot,
            out string error)
        {
            if (response != null
                && !State.Catalog.MatchesServerRevision(response.catalogRevision))
            {
                snapshot = null;
                error = ClientMessages.Text(messageTexts, "item_catalog_update_required");
                State.SetCatalogError("item_catalog_update_required", error, true);
                return false;
            }

            return InventorySnapshotMapper.TryMap(response, State.Catalog, out snapshot, out error);
        }

        private bool TryMapRecovery(
            CharacterRecoverySnapshotResponse response,
            out CharacterRecoverySnapshot snapshot,
            out string error)
        {
            if (response != null
                && !State.Catalog.MatchesServerRevision(response.catalogRevision))
            {
                snapshot = null;
                error = ClientMessages.Text(messageTexts, "item_catalog_update_required");
                State.SetCatalogError("item_catalog_update_required", error, true);
                return false;
            }

            return InventorySnapshotMapper.TryMap(response, State.Catalog, out snapshot, out error);
        }

        private bool ObserveSessionIdentity()
        {
            var accountId = ClientSession.Auth?.accountId ?? string.Empty;
            var characterId = ClientSession.SelectedCharacter?.id ?? string.Empty;
            if (string.Equals(accountId, observedAccountId, StringComparison.Ordinal)
                && string.Equals(characterId, observedCharacterId, StringComparison.Ordinal))
            {
                return false;
            }

            observedAccountId = accountId;
            observedCharacterId = characterId;
            AbandonRefresh();
            sentOperationId = Guid.Empty;
            lastCompletedOperationId = Guid.Empty;
            serverRevisionFollower.Reset();
            if (!Guid.TryParse(characterId, out var parsedCharacterId)
                || parsedCharacterId == Guid.Empty
                || string.IsNullOrWhiteSpace(accountId))
            {
                State.ClearAll();
                return true;
            }

            State.PrepareCharacter(parsedCharacterId);
            return true;
        }

        private bool TryGetSelectedCharacterId(out Guid characterId)
        {
            return Guid.TryParse(
                    ClientSession.SelectedCharacter?.id,
                    out characterId)
                && characterId != Guid.Empty;
        }

        // A refresh of the previous account or character, and the refresh queued after it, never
        // reach the next one: the routine ends at its next response, and nothing waits for it.
        private void AbandonRefresh()
        {
            refreshGeneration++;
            refreshCoroutine = null;
            refreshInProgress = false;
            queuedRefreshScope = null;
            queuedOperationRefresh = false;
            queuedMinimumRevision = -1;
        }

        private void QueueRefresh(
            InventoryRefreshScope scope,
            bool operationRefresh,
            long minimumRevision)
        {
            if (queuedRefreshScope == null
                || scope == InventoryRefreshScope.Full
                || queuedRefreshScope != InventoryRefreshScope.Full)
            {
                queuedRefreshScope = scope;
            }

            queuedOperationRefresh |= operationRefresh;
            queuedMinimumRevision = Math.Max(queuedMinimumRevision, minimumRevision);
        }

        private void StartQueuedRefresh()
        {
            if (queuedRefreshScope == null)
            {
                return;
            }

            var scope = queuedRefreshScope.Value;
            var operationRefresh = queuedOperationRefresh;
            var minimumRevision = queuedMinimumRevision;
            queuedRefreshScope = null;
            queuedOperationRefresh = false;
            queuedMinimumRevision = -1;
            BeginRefresh(scope, operationRefresh, minimumRevision);
        }

        // A snapshot that cannot be read fails like an invalid response, with the mapper's
        // sentence as its diagnostic.
        internal static ApiError InvalidResponse(string message)
        {
            return new ApiError(
                ApiErrorKind.InvalidResponse,
                0,
                "invalid_inventory_response",
                message,
                string.Empty);
        }
    }
}
