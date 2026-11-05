using System.Net;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Gathering grants that continue off the realtime loop. It calls GameService, follows the quest
// log change of a yield that counted for a quest, and posts the completion; it never reads
// loop-confined state.
internal sealed class GatheringAuthorityOperations(
    SimulationGatheringService? gatheringService,
    RealtimeCompletionQueue completions,
    ILogger logger,
    QuestOperationExecutor? questOperations = null)
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

            // The failure may have come after the commit.
            result = new SimulationGatheringResult(
                null,
                new GameServiceApi.SimulationWorkerErrorResponse(
                    GatheringStopCodes.Failed,
                    "SimulationWorker could not complete the gathering cycle."),
                (int)HttpStatusCode.InternalServerError);
        }

        completions.Post(completionHandler, new GatheringCycleCompleted(
            context,
            session,
            node,
            generation,
            request,
            result,
            await FollowQuestLogAsync(session, request, result)));
    }

    // A yield that counted for a quest patches the quest log, and any committed yield reads a
    // stale log again. One whose answer may have been lost after it counted makes the log stale
    // and reads it again.
    private async Task<RealtimeQuestLogChanged?> FollowQuestLogAsync(
        ActiveSimulationSession session,
        SimulationGatheringRequest request,
        SimulationGatheringResult result)
    {
        if (questOperations is null)
        {
            return null;
        }

        try
        {
            if (result.Succeeded)
            {
                return await questOperations.FollowAsync(session, result.Transaction!.QuestLog, CancellationToken.None);
            }

            return request.ResourceActorId is not null && result.MayHaveCommitted
                ? await questOperations.RecoverAsync(session, CancellationToken.None)
                : null;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while following the quest log of simulation session {SimulationSessionId} after a gathering yield.",
                session.SimulationSessionId);
            return null;
        }
    }
}
