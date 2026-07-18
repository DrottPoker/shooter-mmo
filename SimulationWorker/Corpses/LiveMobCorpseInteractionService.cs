using ShooterMmo.GameProtocol;
using SimulationWorker.Auth;
using SimulationWorker.Sessions;

namespace SimulationWorker.Corpses;

public sealed class LiveMobCorpseInteractionService(
    LiveMobCorpseStore corpseStore,
    AuthServiceClient authServiceClient)
{
    public Task<SimulationCorpseInteractionResult> OpenAsync(
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.IsSyntheticBot)
        {
            return Task.FromResult(SimulationCorpseInteractionResult.Rejected(
                intent,
                "corpse_interaction_unavailable",
                "Synthetic development sessions cannot interact with live Mob corpses."));
        }

        var snapshot = corpseStore.CreateSnapshot(intent.CorpseId);
        if (snapshot is not null
            && !string.Equals(snapshot.ShardId, session.ShardId, StringComparison.Ordinal))
        {
            return Task.FromResult(SimulationCorpseInteractionResult.Rejected(
                intent,
                "wrong_simulation_worker",
                "The live Mob corpse belongs to another Shard.",
                shouldCloseView: true));
        }

        return Task.FromResult(snapshot is null
            ? SimulationCorpseInteractionResult.Rejected(
                intent,
                "corpse_not_found",
                "The live Mob corpse is not active.",
                shouldCloseView: true)
            : SimulationCorpseInteractionResult.Opened(intent, snapshot));
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
                "Synthetic development sessions cannot interact with live Mob corpses.");
        }

        if (intent.OperationKind != RealtimeCorpseInteractionKind.LootItem)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "live_mob_corpse_operation_unsupported",
                "Live Mob corpses support only whole-item loot claims.");
        }

        if (!corpseStore.TryGetLootEntry(
                intent.CorpseId,
                intent.ItemInstanceId,
                out var corpse,
                out var lootEntry)
            || corpse is null
            || lootEntry is null)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "corpse_not_found",
                "The live Mob corpse or loot entry is not active.",
                requiresCorpseRefresh: true,
                shouldCloseView: corpse is null);
        }

        if (!string.Equals(corpse.ShardId, session.ShardId, StringComparison.Ordinal))
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "wrong_simulation_worker",
                "The live Mob corpse belongs to another Shard.",
                shouldCloseView: true);
        }

        if (intent.ExpectedCorpseRevision != corpse.Revision
            || intent.ExpectedItemRevision != 0)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "item_state_conflict",
                "The live Mob corpse state changed before the loot claim.",
                requiresCorpseRefresh: true,
                requiresInventoryRefresh: true);
        }

        if (intent.DestinationContainerId == Guid.Empty
            || intent.ExpectedDestinationContainerRevision < 0
            || intent.DestinationSlotIndex < 0
            || intent.TargetItemInstanceId != Guid.Empty
            || intent.Quantity > 0)
        {
            return SimulationCorpseInteractionResult.Rejected(
                intent,
                "live_mob_corpse_operation_unsupported",
                "Live Mob loot must target one empty player inventory slot as a whole item.");
        }

        var response = await authServiceClient.GrantLiveMobLootAsync(
            session.SimulationSessionId,
            new SimulationMobLootGrantRequest(
                lootEntry.GrantId,
                session.AccountId,
                session.CharacterId,
                session.CarryState.ItemStateRevision,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                corpse.CorpseId,
                corpse.SourceActorDefinitionId,
                lootEntry.DefinitionId,
                lootEntry.Quantity,
                intent.DestinationContainerId,
                intent.ExpectedDestinationContainerRevision,
                intent.DestinationSlotIndex),
            cancellationToken);
        if (!response.Succeeded)
        {
            return FromError(intent, response.Error!);
        }

        var snapshot = corpseStore.CompleteClaim(
            corpse.CorpseId,
            lootEntry.LootEntryId,
            lootEntry.GrantId);
        return SimulationCorpseInteractionResult.Committed(
            intent,
            new CorpseMutationResponse(response.Value!, snapshot));
    }

    private static SimulationCorpseInteractionResult FromError(
        RealtimeCorpseInteractionIntent intent,
        SimulationWorkerErrorResponse error)
    {
        var requiresRefresh = error.Code is "item_state_conflict"
            or "item_slot_occupied"
            or "item_stack_limit_exceeded";
        return SimulationCorpseInteractionResult.Rejected(
            intent,
            error.Code,
            error.Message,
            requiresCorpseRefresh: requiresRefresh,
            requiresInventoryRefresh: true,
            shouldCloseView: error.Code is "corpse_not_found" or "wrong_simulation_worker",
            shouldDisconnect: error.Code is "worker_runtime_changed"
                or "simulation_session_invalid"
                or "auth_service_authentication_failed"
                or "invalid_auth_response");
    }
}
