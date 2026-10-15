using Microsoft.Extensions.Logging;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// Session authority work that continues off the realtime loop. It only calls
// synchronized services, posts completions and logs, so it never touches loop state.
internal sealed class SessionAuthorityOperations(
    SimulationSessionReleaseService releaseService,
    RealtimeOperationTracker operationTracker,
    ILogger logger)
{
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
