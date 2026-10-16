using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Runs world interaction authority for one queued intent and posts its completion.
// Open, Close and every rejection complete synchronously on the calling loop thread;
// only capability dispatch can continue off the loop, after which this class reads no
// loop state.
internal sealed class WorldInteractionAuthorityOperations(
    WorldInteractionAuthorityService? worldInteractionAuthority,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    public async Task RunAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        PlayerSimulationEntity entity,
        RealtimeWorldInteractionIntent intent)
    {
        // The loop may refresh the entity's session during capability dispatch, so the
        // session is read here, on the loop, before the first await.
        var session = entity.Session;
        WorldInteractionAuthorityResult result;
        try
        {
            result = await worldInteractionAuthority!.ProcessAsync(
                context.PeerId,
                entity,
                intent,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while processing world interaction {OperationId}.",
                intent.OperationId);
            result = new WorldInteractionAuthorityResult(
                null,
                new RealtimeWorldInteractionResult(
                    intent.OperationId,
                    intent.InteractionSessionId,
                    intent.OperationKind,
                    false,
                    0,
                    itemStateRevision: 0,
                    message: string.Empty,
                    error: new RealtimeError(
                        "world_interaction_failed",
                        "SimulationWorker could not complete the world interaction.")),
                null);
        }

        completions.Post(completionHandler, new WorldInteractionOperationCompleted(
            context,
            session,
            result));
    }
}
