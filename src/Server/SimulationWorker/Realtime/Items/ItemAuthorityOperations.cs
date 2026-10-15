using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Item authority work that continues off the realtime loop. It calls GameService and
// posts the completion; it never reads loop-confined state.
internal sealed class ItemAuthorityOperations(
    SimulationItemInteractionService itemInteractionService,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    public async Task RunAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        RealtimeItemOperationIntent intent,
        float positionX,
        float positionY,
        float positionZ)
    {
        try
        {
            var result = await itemInteractionService.ExecuteAsync(
                session,
                intent,
                positionX,
                positionY,
                positionZ,
                CancellationToken.None);
            completions.Post(completionHandler, new ItemOperationCompleted(
                context,
                session,
                result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while mutating items for simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            completions.Post(completionHandler, new ItemOperationCompleted(
                context,
                session,
                SimulationItemInteractionResult.Rejected(
                    intent,
                    "item_operation_failed",
                    "SimulationWorker could not complete the item operation.")));
        }
    }
}
