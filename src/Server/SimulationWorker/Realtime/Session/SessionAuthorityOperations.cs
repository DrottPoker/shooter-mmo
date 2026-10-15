using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Session authority work that continues off the realtime loop. It only calls
// synchronized services, posts completions and logs, so it never touches loop state.
internal sealed class SessionAuthorityOperations(
    SimulationJoinService joinService,
    SimulationSessionReleaseService releaseService,
    RealtimeCompletionQueue completions,
    RealtimeOperationTracker operationTracker,
    ILogger logger)
{
    public async Task RunJoinAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        string joinTicket)
    {
        try
        {
            var result = await joinService.JoinAsync(joinTicket, CancellationToken.None);
            completions.Post(completionHandler, new JoinCompleted(
                context,
                result,
                Stopwatch.GetTimestamp()));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while authenticating UDP peer {PeerId}.",
                context.PeerId);
            completions.Post(completionHandler, new JoinCompleted(
                context,
                SimulationJoinResult<ActiveSimulationSession>.Failure(
                    500,
                    "join_failed",
                    "SimulationWorker could not complete the join request."),
                Stopwatch.GetTimestamp()));
        }
    }

    public async Task RunLeaveAsync(
        IRealtimeCompletionHandler completionHandler,
        RealtimePeerContext context,
        ActiveSimulationSession session)
    {
        try
        {
            var result = await releaseService.ReleaseAsync(session, CancellationToken.None);
            completions.Post(completionHandler, new LeaveCompleted(context, session, result));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[SIMULATION] Unhandled error while releasing UDP peer {PeerId}.",
                context.PeerId);
            completions.Post(completionHandler, new LeaveCompleted(
                context,
                session,
                SimulationSessionReleaseResult.Failure(
                    "leave_failed",
                    "SimulationWorker could not complete the leave request.")));
        }
    }

    // Runs the synchronous prefix on the calling loop thread, which removes the local
    // session before the first await, so the heartbeat stops renewing it at once.
    public void ReleaseDetachedInBackground(ActiveSimulationSession session)
    {
        operationTracker.Track(ReleaseDetachedAsync(session));
    }

    public async Task ReleaseDetachedAsync(ActiveSimulationSession session)
    {
        try
        {
            var result = await releaseService.ReleaseDetachedAsync(session, CancellationToken.None);
            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "[SIMULATION] Could not release disconnected simulation session {SimulationSessionId}: {Code}. Its database lease expires without renewal.",
                    session.SimulationSessionId,
                    result.Code);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[SIMULATION] Could not release disconnected simulation session {SimulationSessionId}.",
                session.SimulationSessionId);
        }
    }
}
