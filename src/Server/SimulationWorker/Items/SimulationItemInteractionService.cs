using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Items;

// Forwards one realtime item operation to GameService with the item service access that
// the realtime loop evaluated when the operation started.
public sealed class SimulationItemInteractionService(GameServiceClient gameServiceClient)
{
    public async Task<SimulationItemInteractionResult> ExecuteAsync(
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        ItemInteractionAccess access,
        CancellationToken cancellationToken,
        long? expectedCharacterRevision = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(access);

        if (session.IsSyntheticBot)
        {
            return SimulationItemInteractionResult.Rejected(
                intent,
                "item_operation_unavailable",
                "Synthetic development sessions cannot mutate durable item state.");
        }

        // The realtime loop forwards an intent whose revision only the worker's own arrow
        // batches advanced with the newest revision.
        var request = CreateRequest(session, intent, access) with
        {
            ExpectedCharacterRevision = expectedCharacterRevision ?? intent.ExpectedCharacterRevision
        };
        var result = await gameServiceClient.MutateSimulationItemsAsync(
            session.SimulationSessionId,
            request,
            cancellationToken);
        if (result.Succeeded)
        {
            return SimulationItemInteractionResult.Committed(intent, result.Value!);
        }

        var error = result.Error!;
        return SimulationItemInteractionResult.Rejected(
            intent,
            error.Code,
            error.Message,
            RequiresInventoryRefresh(error.Code),
            GameServiceOutcome.EndsItemSession(error.Code));
    }

    private static SimulationItemOperationRequest CreateRequest(
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        ItemInteractionAccess access)
    {
        var operationKind = ToOperationKind(intent.OperationKind);
        int? destinationSlotIndex = intent.DestinationSlotIndex < 0
            ? null
            : intent.DestinationSlotIndex;
        var items = intent.OperationKind == RealtimeItemOperationKind.ClaimRecoveryDelivery
            ? intent.Items
                .Select(item => new SimulationItemRevisionExpectation(
                    item.ItemInstanceId,
                    item.Revision))
                .ToArray()
            : null;

        return new SimulationItemOperationRequest(
            intent.OperationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(
                access.Bank,
                access.RecoveryStorage,
                false),
            operationKind,
            intent.ExpectedCharacterRevision,
            intent.ItemInstanceId == Guid.Empty ? null : intent.ItemInstanceId,
            intent.ItemInstanceId == Guid.Empty ? null : intent.ExpectedItemRevision,
            intent.TargetItemInstanceId == Guid.Empty ? null : intent.TargetItemInstanceId,
            intent.TargetItemInstanceId == Guid.Empty
                ? null
                : intent.ExpectedTargetItemRevision,
            intent.DestinationContainerId == Guid.Empty
                ? null
                : intent.DestinationContainerId,
            destinationSlotIndex,
            intent.Quantity <= 0 ? null : intent.Quantity,
            string.IsNullOrWhiteSpace(intent.EquipmentSlotId)
                ? null
                : intent.EquipmentSlotId,
            intent.RecoveryDeliveryId == Guid.Empty ? null : intent.RecoveryDeliveryId,
            intent.RecoveryDeliveryId == Guid.Empty
                ? null
                : intent.ExpectedRecoveryDeliveryRevision,
            items);
    }

    private static string ToOperationKind(RealtimeItemOperationKind operationKind)
    {
        return operationKind switch
        {
            RealtimeItemOperationKind.Relocate => "relocate",
            RealtimeItemOperationKind.Equip => "equip",
            RealtimeItemOperationKind.Unequip => "unequip",
            RealtimeItemOperationKind.SplitStack => "split_stack",
            RealtimeItemOperationKind.MergeStacks => "merge_stacks",
            RealtimeItemOperationKind.SwapContainerItems => "swap_container_items",
            RealtimeItemOperationKind.Destroy => "destroy",
            RealtimeItemOperationKind.ClaimRecoveryDelivery => "claim_recovery_delivery",
            _ => throw new ArgumentOutOfRangeException(
                nameof(operationKind),
                operationKind,
                "Unsupported realtime item operation kind.")
        };
    }

    private static bool RequiresInventoryRefresh(string code)
    {
        return GameServiceOutcome.IsUncertain(code)
            || code is "item_state_conflict"
            or "item_operation_conflict"
            or "item_quantity_changed"
            or "bag_state_changed"
            or "item_not_found"
            or "item_not_owned";
    }
}

public sealed record SimulationItemInteractionResult(
    RealtimeItemOperationIntent Intent,
    SimulationItemTransactionResponse? Transaction,
    SimulationWorkerErrorResponse? Error,
    bool RequiresInventoryRefresh,
    bool ShouldDisconnect)
{
    public bool Succeeded => Transaction is not null && Error is null;

    public static SimulationItemInteractionResult Committed(
        RealtimeItemOperationIntent intent,
        SimulationItemTransactionResponse transaction)
    {
        return new SimulationItemInteractionResult(
            intent,
            transaction,
            null,
            false,
            false);
    }

    public static SimulationItemInteractionResult Rejected(
        RealtimeItemOperationIntent intent,
        string code,
        string message,
        bool requiresInventoryRefresh = false,
        bool shouldDisconnect = false)
    {
        return new SimulationItemInteractionResult(
            intent,
            null,
            new SimulationWorkerErrorResponse(code, message),
            requiresInventoryRefresh,
            shouldDisconnect);
    }
}
