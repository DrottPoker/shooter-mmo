using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Corpse authority work that continues off the realtime loop: opens, refreshes and
// mutations through GameService or the live Mob corpse service. Mutations of one corpse
// are serialized by the mutation coordinator. It never reads loop-confined state.
internal sealed class CorpseAuthorityOperations(
    SimulationCorpseInteractionService corpseInteractionService,
    LiveMobCorpseInteractionService? liveMobCorpseInteractionService,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    public CorpseMutationCoordinator MutationCoordinator { get; } = new();

    public async Task RunAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        bool removeOpenReservationOnFailure,
        bool isLiveMobCorpse)
    {
        try
        {
            var interactionService = isLiveMobCorpse
                ? liveMobCorpseInteractionService
                    ?? throw new InvalidOperationException(
                        "Live Mob corpse interaction is not configured.")
                : null;
            var result = intent.OperationKind switch
            {
                RealtimeCorpseInteractionKind.Open
                    or RealtimeCorpseInteractionKind.Refresh =>
                    isLiveMobCorpse
                        ? await interactionService!.OpenAsync(
                            session,
                            intent,
                            CancellationToken.None)
                        : await corpseInteractionService.OpenAsync(
                            session,
                            intent,
                            CancellationToken.None),
                RealtimeCorpseInteractionKind.LootItem
                    or RealtimeCorpseInteractionKind.LootPartialStack
                    or RealtimeCorpseInteractionKind.LootToEquipment
                    or RealtimeCorpseInteractionKind.DepositItem
                    or RealtimeCorpseInteractionKind.DepositPartialStack
                    or RealtimeCorpseInteractionKind.MoveItem
                    or RealtimeCorpseInteractionKind.MovePartialStack
                    or RealtimeCorpseInteractionKind.SwapBag
                    or RealtimeCorpseInteractionKind.LootAll =>
                    await CompleteCorpseMutationAsync(
                        interactionService,
                        session,
                        intent,
                        isLiveMobCorpse),
                _ => throw new InvalidOperationException(
                    "The queued corpse operation is not supported by GameService.")
            };
            completions.Post(completionHandler, new CorpseOperationCompleted(
                context,
                session,
                result,
                removeOpenReservationOnFailure));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while interacting with corpse {CorpseId} for simulation session {SimulationSessionId}.",
                intent.CorpseId,
                session.SimulationSessionId);
            completions.Post(completionHandler, new CorpseOperationCompleted(
                context,
                session,
                SimulationCorpseInteractionResult.Rejected(
                    intent,
                    "corpse_operation_failed",
                    "SimulationWorker could not complete the corpse operation.",
                    requiresCorpseRefresh: true),
                removeOpenReservationOnFailure));
        }
    }

    private async Task<SimulationCorpseInteractionResult> CompleteCorpseMutationAsync(
        LiveMobCorpseInteractionService? interactionService,
        ActiveSimulationSession session,
        RealtimeCorpseInteractionIntent intent,
        bool isLiveMobCorpse)
    {
        using var mutationLease = await MutationCoordinator.AcquireAsync(
            intent.CorpseId,
            CancellationToken.None);
        return isLiveMobCorpse
            ? await interactionService!.MutateAsync(
                session,
                intent,
                CancellationToken.None)
            : await corpseInteractionService.MutateAsync(
                session,
                intent,
                CancellationToken.None);
    }
}
