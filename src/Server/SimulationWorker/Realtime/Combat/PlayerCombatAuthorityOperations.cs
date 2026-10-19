using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Realtime;

internal sealed record AmmunitionBatchCompleted(
    RealtimePeerContext PeerContext,
    PlayerCombatSession CombatSession,
    AmmunitionBatch Batch,
    GameServiceResult<SimulationItemTransactionResponse> Result)
    : RealtimeOperationResult(PeerContext);

internal sealed record PlayerDeathCompleted(
    RealtimePeerContext PeerContext,
    PlayerCombatSession CombatSession,
    PlayerDeathAttempt Attempt,
    SimulationPlayerDeathRequest Request,
    GameServiceResult<PlayerDeathPartitionResponse> Result)
    : RealtimeOperationResult(PeerContext);

// Ammunition batches and player deaths that continue off the realtime loop. It calls
// GameService and posts the completion; it never reads loop-confined state.
internal sealed class PlayerCombatAuthorityOperations(
    GameServiceClient? gameServiceClient,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    public async Task ConsumeAmmunitionAsync(
        IRealtimeCompletionHandler handler,
        RealtimePeerContext context,
        PlayerCombatSession combatSession,
        AmmunitionBatch batch,
        SimulationAmmunitionConsumptionRequest request)
    {
        GameServiceResult<SimulationItemTransactionResponse> result;
        try
        {
            result = gameServiceClient is null
                ? Unavailable<SimulationItemTransactionResponse>()
                : await gameServiceClient.ConsumeAmmunitionAsync(
                    combatSession.Session.SimulationSessionId,
                    request,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while committing spent arrows for simulation session {SimulationSessionId}.",
                combatSession.Session.SimulationSessionId);
            result = Failed<SimulationItemTransactionResponse>();
        }

        completions.Post(handler, new AmmunitionBatchCompleted(context, combatSession, batch, result));
    }

    public async Task ProcessDeathAsync(
        IRealtimeCompletionHandler handler,
        RealtimePeerContext context,
        PlayerCombatSession combatSession,
        PlayerDeathAttempt attempt,
        SimulationPlayerDeathRequest request)
    {
        GameServiceResult<PlayerDeathPartitionResponse> result;
        try
        {
            result = gameServiceClient is null
                ? Unavailable<PlayerDeathPartitionResponse>()
                : await gameServiceClient.ProcessPlayerDeathAsync(
                    combatSession.Session.SimulationSessionId,
                    request,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while committing the death of simulation session {SimulationSessionId}.",
                combatSession.Session.SimulationSessionId);
            result = Failed<PlayerDeathPartitionResponse>();
        }

        completions.Post(handler, new PlayerDeathCompleted(context, combatSession, attempt, request, result));
    }

    private static GameServiceResult<T> Unavailable<T>()
    {
        return GameServiceResult<T>.Failure(
            503,
            "auth_service_unavailable",
            "SimulationWorker has no GameService client for combat authority.");
    }

    private static GameServiceResult<T> Failed<T>()
    {
        return GameServiceResult<T>.Failure(
            503,
            "auth_service_unavailable",
            "SimulationWorker could not complete the combat authority operation.");
    }
}
