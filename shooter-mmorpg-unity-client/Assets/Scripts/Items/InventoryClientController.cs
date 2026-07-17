using System;
using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Diagnostics;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Items
{
    public sealed class InventoryPendingOperation
    {
        public InventoryPendingOperation(
            RealtimeItemOperationIntent intent,
            InventoryRefreshScope rejectionRefreshScope)
        {
            Intent = intent;
            RejectionRefreshScope = rejectionRefreshScope;
        }

        public RealtimeItemOperationIntent Intent { get; }

        public InventoryRefreshScope RejectionRefreshScope { get; }

        public bool AwaitingRefresh { get; private set; }

        public bool Uncertain { get; private set; }

        public void MarkAwaitingRefresh()
        {
            AwaitingRefresh = true;
        }

        public void MarkUncertain()
        {
            Uncertain = true;
        }
    }

    public sealed class InventoryOperationJournal
    {
        private Guid lastCompletedOperationId;
        private RealtimeItemOperationKind lastCompletedOperationKind;

        public InventoryPendingOperation Pending { get; private set; }

        public bool TryBegin(
            RealtimeItemOperationIntent intent,
            InventoryRefreshScope rejectionRefreshScope)
        {
            if (Pending != null || intent == null || intent.OperationId == Guid.Empty)
            {
                return false;
            }

            Pending = new InventoryPendingOperation(intent, rejectionRefreshScope);
            return true;
        }

        public bool Matches(RealtimeItemOperationResult result)
        {
            return Pending != null
                && result != null
                && Pending.Intent.OperationId == result.OperationId
                && Pending.Intent.OperationKind == result.OperationKind;
        }

        public bool IsDuplicateCompletion(RealtimeItemOperationResult result)
        {
            return result != null
                && result.OperationId == lastCompletedOperationId
                && result.OperationKind == lastCompletedOperationKind;
        }

        public void MarkAwaitingRefresh()
        {
            Pending?.MarkAwaitingRefresh();
        }

        public void MarkUncertain()
        {
            Pending?.MarkUncertain();
        }

        public void Complete()
        {
            if (Pending != null)
            {
                lastCompletedOperationId = Pending.Intent.OperationId;
                lastCompletedOperationKind = Pending.Intent.OperationKind;
            }

            Pending = null;
        }

        public void Reset()
        {
            Pending = null;
            lastCompletedOperationId = Guid.Empty;
            lastCompletedOperationKind = default;
        }
    }

    [DisallowMultipleComponent]
    public sealed class InventoryClientController : MonoBehaviour
    {
        private const int MaximumRefreshAttempts = 3;
        private const float RefreshRetryDelaySeconds = 0.1f;

        private readonly InventoryOperationJournal operationJournal =
            new InventoryOperationJournal();

        private ShooterMmoApiClient apiClient;
        private RealtimeSimulationClient simulationClient;
        private Coroutine refreshCoroutine;
        private InventoryRefreshScope? queuedRefreshScope;
        private long queuedMinimumRevision = -1;
        private bool queuedOperationRefresh;
        private bool initialized;
        private bool observedRealtimeJoin;
        private string observedAccountId = string.Empty;
        private string observedCharacterId = string.Empty;

        public InventoryClientState State { get; } = new InventoryClientState();

        public InventoryOperationJournal OperationJournal
        {
            get { return operationJournal; }
        }

        public bool IsInitialized
        {
            get { return initialized; }
        }

        public void Initialize(
            ShooterMmoApiClient client,
            RealtimeSimulationClient realtimeClient,
            TextAsset gameplayCatalog)
        {
            if (initialized)
            {
                return;
            }

            apiClient = client ?? throw new ArgumentNullException(nameof(client));
            simulationClient = realtimeClient
                ?? throw new ArgumentNullException(nameof(realtimeClient));
            if (ClientItemCatalog.TryLoad(gameplayCatalog, out var catalog, out var error))
            {
                State.SetCatalog(catalog);
            }
            else
            {
                State.SetCatalogError("item_catalog_invalid", error, false);
            }

            simulationClient.ItemOperationCompleted += OnItemOperationCompleted;
            simulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
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
            }
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.ItemOperationCompleted -= OnItemOperationCompleted;
                simulationClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            }

            if (refreshCoroutine != null)
            {
                StopCoroutine(refreshCoroutine);
                refreshCoroutine = null;
            }
        }

        public void EnsureFullState()
        {
            if (!TryGetSelectedCharacterId(out _))
            {
                State.SetError(
                    new InventoryClientError(
                        "inventory_character_unavailable",
                        "Select and join a character before opening inventory."),
                    true);
                return;
            }

            if (State.Catalog == null
                || (State.HasCoherentFullSnapshot
                    && State.Status == InventoryClientStatus.Ready))
            {
                return;
            }

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
            if (!TryPrepareItemOperation(item, out var source, out error))
            {
                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateRelocate(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                item.ItemInstanceId,
                item.Revision,
                destinationContainerId,
                destinationSlotIndex);
            return TrySubmit(intent, DetermineRefreshScope(source, destinationContainerId), out error);
        }

        public bool TryEquip(
            InventoryItem item,
            string equipmentSlotId,
            out string error)
        {
            if (!TryPrepareItemOperation(item, out _, out error))
            {
                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateEquip(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                item.ItemInstanceId,
                item.Revision,
                equipmentSlotId);
            return TrySubmit(intent, InventoryRefreshScope.Full, out error);
        }

        public bool TryUnequip(
            InventoryItem item,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error)
        {
            if (!TryPrepareItemOperation(item, out _, out error))
            {
                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateUnequip(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                item.ItemInstanceId,
                item.Revision,
                destinationContainerId,
                destinationSlotIndex);
            return TrySubmit(intent, InventoryRefreshScope.Full, out error);
        }

        public bool TrySplit(
            InventoryItem item,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex,
            out string error)
        {
            if (!TryPrepareItemOperation(item, out var source, out error)
                || !InventoryTargetAdvisor.CanSplit(State.Catalog, item, quantity, out error))
            {
                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateSplitStack(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                item.ItemInstanceId,
                item.Revision,
                quantity,
                destinationContainerId,
                destinationSlotIndex);
            return TrySubmit(intent, DetermineRefreshScope(source, destinationContainerId), out error);
        }

        public bool TryMerge(
            InventoryItem sourceItem,
            InventoryItem targetItem,
            out string error)
        {
            if (!TryPrepareItemOperation(sourceItem, out var source, out error)
                || !State.TryFindItem(targetItem.ItemInstanceId, out var currentTarget, out var target)
                || currentTarget.Revision != targetItem.Revision
                || !InventoryTargetAdvisor.CanMerge(
                    State,
                    sourceItem,
                    source,
                    currentTarget,
                    target,
                    out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The target stack is stale or unavailable.";
                }

                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateMergeStacks(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                sourceItem.ItemInstanceId,
                sourceItem.Revision,
                targetItem.ItemInstanceId,
                targetItem.Revision);
            var scope = source.IsExternal && target.IsExternal
                ? InventoryRefreshScope.Bank
                : InventoryRefreshScope.Full;
            return TrySubmit(intent, scope, out error);
        }

        public bool TrySwap(
            InventoryItem firstItem,
            InventoryItem secondItem,
            out string error)
        {
            if (!TryPrepareItemOperation(firstItem, out var firstLocation, out error)
                || !State.TryFindItem(
                    secondItem.ItemInstanceId,
                    out var currentSecondItem,
                    out var secondLocation)
                || currentSecondItem.Revision != secondItem.Revision
                || !InventoryTargetAdvisor.CanSwap(
                    State,
                    firstItem,
                    firstLocation,
                    currentSecondItem,
                    secondLocation,
                    out error))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The swap target is stale or unavailable.";
                }

                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateSwapContainerItems(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                firstItem.ItemInstanceId,
                firstItem.Revision,
                currentSecondItem.ItemInstanceId,
                currentSecondItem.Revision);
            var scope = firstLocation.IsExternal && secondLocation.IsExternal
                ? InventoryRefreshScope.Bank
                : InventoryRefreshScope.Full;
            return TrySubmit(intent, scope, out error);
        }

        public bool TryDestroy(InventoryItem item, out string error)
        {
            if (!TryPrepareItemOperation(item, out var source, out error)
                || !InventoryTargetAdvisor.CanDestroy(State.Catalog, item, out error))
            {
                return false;
            }

            var intent = RealtimeItemOperationIntent.CreateDestroy(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                item.ItemInstanceId,
                item.Revision);
            return TrySubmit(
                intent,
                source.IsExternal ? InventoryRefreshScope.Bank : InventoryRefreshScope.Full,
                out error);
        }

        public bool TryClaimRecovery(
            RecoveryDelivery delivery,
            Guid destinationContainerId,
            out string error)
        {
            error = string.Empty;
            if (!CanStartOperation(out error) || delivery == null || delivery.Items.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The Recovery delivery is empty or unavailable.";
                }

                return false;
            }

            var expectations = new RealtimeItemRevisionExpectation[delivery.Items.Count];
            for (var index = 0; index < delivery.Items.Count; index++)
            {
                expectations[index] = new RealtimeItemRevisionExpectation(
                    delivery.Items[index].Item.ItemInstanceId,
                    delivery.Items[index].Item.Revision);
            }

            var intent = RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                Guid.NewGuid(),
                State.KnownItemStateRevision,
                delivery.DeliveryId,
                delivery.Revision,
                destinationContainerId,
                expectations);
            return TrySubmit(intent, InventoryRefreshScope.Full, out error);
        }

        private bool TryPrepareItemOperation(
            InventoryItem item,
            out InventoryItemLocation location,
            out string error)
        {
            location = null;
            if (!CanStartOperation(out error)
                || item == null
                || !State.TryFindItem(item.ItemInstanceId, out var current, out location)
                || current.Revision != item.Revision)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The selected item is stale or unavailable.";
                }

                return false;
            }

            return true;
        }

        private bool CanStartOperation(out string error)
        {
            error = string.Empty;
            if (!initialized
                || State.Catalog == null
                || !State.CanMutate
                || simulationClient == null
                || !simulationClient.IsJoined)
            {
                error = "Inventory must finish loading in an active simulation session.";
                return false;
            }

            if (operationJournal.Pending != null)
            {
                error = "Another item operation is still pending.";
                return false;
            }

            return true;
        }

        private bool TrySubmit(
            RealtimeItemOperationIntent intent,
            InventoryRefreshScope rejectionRefreshScope,
            out string error)
        {
            error = string.Empty;
            if (!operationJournal.TryBegin(intent, rejectionRefreshScope))
            {
                error = "Another item operation is still pending.";
                return false;
            }

            if (!simulationClient.TrySendItemOperation(intent))
            {
                operationJournal.Reset();
                error = "The item operation could not be sent to SimulationWorker.";
                return false;
            }

            State.BeginLoading(true);
            return true;
        }

        private void OnItemOperationCompleted(RealtimeItemOperationResult result)
        {
            if (!operationJournal.Matches(result))
            {
                if (operationJournal.IsDuplicateCompletion(result))
                {
                    return;
                }

                operationJournal.Reset();
                State.SetError(
                    new InventoryClientError(
                        "inventory_operation_result_mismatch",
                        "The item operation result did not match the pending operation."),
                    true);
                BeginRefresh(InventoryRefreshScope.Full, true, -1);
                return;
            }

            var pending = operationJournal.Pending;
            if (!result.Succeeded)
            {
                var error = new InventoryClientError(
                    result.Error?.Code ?? "item_operation_rejected",
                    result.Error?.Message ?? "The server rejected the item operation.");
                if (!result.RequiresInventoryRefresh)
                {
                    operationJournal.Complete();
                    State.SetServerRejection(error);
                    return;
                }

                State.SetError(error, false);
                operationJournal.MarkAwaitingRefresh();
                BeginRefresh(
                    pending.RejectionRefreshScope,
                    true,
                    result.CarryState.ItemStateRevision);
                return;
            }

            operationJournal.MarkAwaitingRefresh();
            BeginRefresh(
                InventoryRefreshScope.Full,
                true,
                result.CarryState.ItemStateRevision);
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            var pending = operationJournal.Pending != null;
            if (pending)
            {
                operationJournal.MarkUncertain();
            }

            State.MarkDisconnected(pending);
            operationJournal.Reset();
            observedRealtimeJoin = false;
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

            if (refreshCoroutine != null)
            {
                QueueRefresh(scope, operationRefresh, minimumRevision);
                return;
            }

            refreshCoroutine = StartCoroutine(
                RefreshRoutine(scope, operationRefresh, minimumRevision));
        }

        private IEnumerator RefreshRoutine(
            InventoryRefreshScope scope,
            bool operationRefresh,
            long minimumRevision)
        {
            State.BeginLoading(operationRefresh);
            var succeeded = false;
            for (var attempt = 0; attempt < MaximumRefreshAttempts; attempt++)
            {
                if (!TryGetSelectedCharacterId(out var characterId))
                {
                    break;
                }

                var token = ShooterMmoClientSession.SessionToken;
                ShooterMmoApiError apiError = null;
                InventorySnapshotApplyResult applyResult;
                long receivedRevision;

                if (scope == InventoryRefreshScope.Bank)
                {
                    CharacterBankSnapshotResponse response = null;
                    var mapError = string.Empty;
                    yield return apiClient.GetCharacterBank(
                        ShooterMmoClientSession.AuthServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
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
                        ShooterMmoClientSession.AuthServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
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
                        ShooterMmoClientSession.AuthServiceBaseUrl,
                        token,
                        characterId.ToString(),
                        value => response = value,
                        value => apiError = value);
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
                        && !ClientSessionRecovery.ReturnToLoginIfUnauthorized(apiError))
                    {
                        State.SetError(
                            new InventoryClientError(
                                apiError.Code,
                                apiError.Message,
                                apiError.CorrelationId),
                            true);
                    }

                    break;
                }

                if (applyResult == InventorySnapshotApplyResult.Diverged)
                {
                    State.SetError(
                        new InventoryClientError(
                            "inventory_state_diverged",
                            "Equal item-state revisions contained different inventory state."),
                        true);
                    break;
                }

                if (applyResult == InventorySnapshotApplyResult.WrongCharacter)
                {
                    State.SetError(
                        new InventoryClientError(
                            "inventory_character_changed",
                            "The inventory response belongs to a different character."),
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
                }
            }

            if (!succeeded
                && (State.Status is InventoryClientStatus.Loading
                    or InventoryClientStatus.Busy))
            {
                State.SetError(
                    new InventoryClientError(
                        "inventory_refresh_stale",
                        "Authoritative inventory state did not reach the required revision."),
                    true);
            }

            if (operationJournal.Pending != null
                && operationJournal.Pending.AwaitingRefresh)
            {
                operationJournal.Complete();
            }

            refreshCoroutine = null;
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
                error = "This client build requires an item catalog update.";
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
                error = "This client build requires an item catalog update.";
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
                error = "This client build requires an item catalog update.";
                State.SetCatalogError("item_catalog_update_required", error, true);
                return false;
            }

            return InventorySnapshotMapper.TryMap(response, State.Catalog, out snapshot, out error);
        }

        private bool ObserveSessionIdentity()
        {
            var accountId = ShooterMmoClientSession.Auth?.accountId ?? string.Empty;
            var characterId = ShooterMmoClientSession.SelectedCharacter?.id ?? string.Empty;
            if (string.Equals(accountId, observedAccountId, StringComparison.Ordinal)
                && string.Equals(characterId, observedCharacterId, StringComparison.Ordinal))
            {
                return false;
            }

            observedAccountId = accountId;
            observedCharacterId = characterId;
            operationJournal.Reset();
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
                    ShooterMmoClientSession.SelectedCharacter?.id,
                    out characterId)
                && characterId != Guid.Empty;
        }

        private InventoryRefreshScope DetermineRefreshScope(
            InventoryItemLocation source,
            Guid destinationContainerId)
        {
            return source != null
                && string.Equals(source.ContainerType, "bank", StringComparison.Ordinal)
                && State.Bank != null
                && State.Bank.ContainerId == destinationContainerId
                    ? InventoryRefreshScope.Bank
                    : InventoryRefreshScope.Full;
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

        private static ShooterMmoApiError InvalidResponse(string message)
        {
            return new ShooterMmoApiError(
                ShooterMmoApiErrorKind.InvalidResponse,
                0,
                "invalid_inventory_response",
                message,
                string.Empty);
        }
    }
}
