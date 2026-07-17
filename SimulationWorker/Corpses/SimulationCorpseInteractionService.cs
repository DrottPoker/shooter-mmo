using ShooterMmo.GameProtocol;
using SimulationWorker.Auth;
using SimulationWorker.Sessions;

namespace SimulationWorker.Corpses;

public sealed class SimulationCorpseInteractionService(AuthServiceClient authServiceClient)
{
    public async Task<SimulationCorpseInteractionResult> OpenAsync(
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intent);
        if (session.IsSyntheticBot)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "corpse_interaction_unavailable",
                "Synthetic development sessions cannot interact with durable corpses.");
        }

        var request = new SimulationCorpseOpenRequest(
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken);
        var response = await authServiceClient.OpenCorpseAsync(
            session.SimulationSessionId,
            intent.CorpseId,
            request,
            cancellationToken);
        return response.Succeeded
            ? SimulationCorpseInteractionResult.Opened(intent, response.Value!)
            : FromError(intent, response.Error!);
    }

    public async Task<SimulationCorpseInteractionResult> MutateAsync(
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intent);
        if (session.IsSyntheticBot)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "corpse_interaction_unavailable",
                "Synthetic development sessions cannot interact with durable corpses.");
        }

        var request = CreateMutationRequest(session, intent);
        var response = await authServiceClient.MutateCorpseAsync(
            session.SimulationSessionId,
            intent.CorpseId,
            request,
            cancellationToken);
        return response.Succeeded
            ? SimulationCorpseInteractionResult.Committed(intent, response.Value!)
            : FromError(intent, response.Error!);
    }

    private static SimulationCorpseMutationRequest CreateMutationRequest(
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent)
    {
        var operationKind = intent.OperationKind switch
        {
            RealtimeCorpseInteractionKind.LootItem => "loot_item",
            RealtimeCorpseInteractionKind.LootPartialStack => "loot_partial_stack",
            RealtimeCorpseInteractionKind.DepositItem => "deposit_item",
            RealtimeCorpseInteractionKind.DepositPartialStack => "deposit_partial_stack",
            RealtimeCorpseInteractionKind.SwapBag => "swap_bag",
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                "The corpse operation is not a durable mutation.")
        };
        return new SimulationCorpseMutationRequest(
            intent.OperationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            operationKind,
            intent.ExpectedCorpseRevision,
            intent.ItemInstanceId == Guid.Empty ? null : intent.ItemInstanceId,
            intent.ItemInstanceId == Guid.Empty ? null : intent.ExpectedItemRevision,
            intent.Quantity <= 0 ? null : intent.Quantity,
            intent.DestinationContainerId == Guid.Empty
                ? null
                : intent.DestinationContainerId,
            intent.DestinationContainerId == Guid.Empty
                ? null
                : intent.ExpectedDestinationContainerRevision,
            intent.DestinationSlotIndex < 0 ? null : intent.DestinationSlotIndex,
            intent.TargetItemInstanceId == Guid.Empty
                ? null
                : intent.TargetItemInstanceId,
            intent.TargetItemInstanceId == Guid.Empty
                ? null
                : intent.ExpectedTargetItemRevision,
            intent.CorpseBagContentsContainerId == Guid.Empty
                ? null
                : intent.CorpseBagContentsContainerId,
            intent.CorpseBagContentsContainerId == Guid.Empty
                ? null
                : intent.ExpectedCorpseBagContentsRevision,
            intent.PlayerBagItemInstanceId == Guid.Empty
                ? null
                : intent.PlayerBagItemInstanceId,
            intent.PlayerBagItemInstanceId == Guid.Empty
                ? null
                : intent.ExpectedPlayerBagRevision,
            intent.PlayerBagContentsContainerId == Guid.Empty
                ? null
                : intent.PlayerBagContentsContainerId,
            intent.PlayerBagContentsContainerId == Guid.Empty
                ? null
                : intent.ExpectedPlayerBagContentsRevision);
    }

    private static SimulationCorpseInteractionResult FromError(
        RealtimeCorpseInteractionIntent intent,
        SimulationWorkerErrorResponse error)
    {
        return SimulationCorpseInteractionResult.Rejected(
            intent,
            error.Code,
            error.Message,
            RequiresCorpseRefresh(error.Code),
            RequiresInventoryRefresh(error.Code),
            ShouldCloseView(error.Code),
            ShouldDisconnect(error.Code));
    }

    private static bool RequiresCorpseRefresh(string code)
    {
        return code is "item_already_looted"
            or "item_quantity_changed"
            or "bag_state_changed"
            or "item_state_conflict"
            or "item_slot_occupied";
    }

    private static bool RequiresInventoryRefresh(string code)
    {
        return RequiresCorpseRefresh(code)
            || code is "carry_weight_limit_exceeded"
                or "item_stack_limit_exceeded"
                or "item_stack_incompatible";
    }

    private static bool ShouldCloseView(string code)
    {
        return code is "corpse_expired"
            or "corpse_invalidated"
            or "corpse_not_found"
            or "wrong_simulation_worker";
    }

    private static bool ShouldDisconnect(string code)
    {
        return code is "worker_runtime_changed"
            or "simulation_session_invalid"
            or "auth_service_authentication_failed"
            or "invalid_auth_response";
    }
}

public sealed record SimulationCorpseInteractionResult(
    RealtimeCorpseInteractionIntent Intent,
    CorpseViewSnapshotResponse? Snapshot,
    CorpseMutationResponse? Mutation,
    SimulationWorkerErrorResponse? Error,
    bool RequiresCorpseRefresh,
    bool RequiresInventoryRefresh,
    bool ShouldCloseView,
    bool ShouldDisconnect)
{
    public bool Succeeded => Error is null
        && (Snapshot is not null
            || Mutation is not null
            || Intent.OperationKind == RealtimeCorpseInteractionKind.Close);

    public static SimulationCorpseInteractionResult Opened(
        RealtimeCorpseInteractionIntent intent,
        CorpseViewSnapshotResponse snapshot)
    {
        return new SimulationCorpseInteractionResult(
            intent,
            snapshot,
            null,
            null,
            false,
            false,
            false,
            false);
    }

    public static SimulationCorpseInteractionResult Committed(
        RealtimeCorpseInteractionIntent intent,
        CorpseMutationResponse mutation)
    {
        return new SimulationCorpseInteractionResult(
            intent,
            mutation.Corpse,
            mutation,
            null,
            false,
            true,
            mutation.Corpse is null,
            false);
    }

    public static SimulationCorpseInteractionResult Closed(
        RealtimeCorpseInteractionIntent intent)
    {
        return new SimulationCorpseInteractionResult(
            intent,
            null,
            null,
            null,
            false,
            false,
            false,
            false);
    }

    public static SimulationCorpseInteractionResult Rejected(
        RealtimeCorpseInteractionIntent intent,
        string code,
        string message,
        bool requiresCorpseRefresh = false,
        bool requiresInventoryRefresh = false,
        bool shouldCloseView = false,
        bool shouldDisconnect = false)
    {
        return new SimulationCorpseInteractionResult(
            intent,
            null,
            null,
            new SimulationWorkerErrorResponse(code, message),
            requiresCorpseRefresh,
            requiresInventoryRefresh,
            shouldCloseView,
            shouldDisconnect);
    }
}
