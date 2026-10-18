using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Gathering grants that continue off the realtime loop. It calls GameService and posts
// the completion; it never reads loop-confined state.
internal sealed class GatheringAuthorityOperations(
    SimulationGatheringService? gatheringService,
    RealtimeCompletionQueue completions,
    ILogger logger)
{
    public async Task RunAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        ActiveSimulationSession session,
        WorldActorRuntimeState node,
        long generation,
        SimulationGatheringRequest request)
    {
        SimulationGatheringResult result;
        try
        {
            result = gatheringService is null
                ? new SimulationGatheringResult(
                    null,
                    new GameServiceApi.SimulationWorkerErrorResponse(
                        "gathering_unavailable",
                        "Gathering is not available on this worker."))
                : await gatheringService.GatherAsync(session, request, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while gathering for simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
            result = new SimulationGatheringResult(
                null,
                new GameServiceApi.SimulationWorkerErrorResponse(
                    GatheringStopCodes.Failed,
                    "SimulationWorker could not complete the gathering cycle."));
        }

        completions.Post(completionHandler, new GatheringCycleCompleted(
            context,
            session,
            node,
            generation,
            request,
            result));
    }
}
