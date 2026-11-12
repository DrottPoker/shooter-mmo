using System.Net;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Sessions;

// Releases simulation sessions through GameService and removes the state the worker kept for
// them from the registered per-character stores.
public sealed class SimulationSessionReleaseService(
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    CharacterSessionStores characterStores,
    IReadOnlyList<TimeSpan>? detachedReleaseRetryDelays = null)
{
    private static readonly TimeSpan[] DefaultDetachedReleaseRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    private readonly IReadOnlyList<TimeSpan> retryDelays =
        detachedReleaseRetryDelays ?? DefaultDetachedReleaseRetryDelays;

    // The stores whose state goes with a released session; session teardown on the realtime
    // loop removes from the same stores.
    public CharacterSessionStores CharacterStores => characterStores;

    // Releases a session that no connected peer owns any more. The local lease is dropped
    // first, so the heartbeat stops renewing it even when GameService stays unavailable
    // and the database lease then expires on its own. The state the join kept for the session
    // goes with it.
    public async Task<SimulationSessionReleaseResult> ReleaseDetachedAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        if (sessionStore.Remove(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken))
        {
            characterStores.Remove(session.CharacterId, session.SimulationSessionId);
        }

        var result = await ReleaseAsync(session, cancellationToken);
        for (var attempt = 0; !result.Succeeded && attempt < retryDelays.Count; attempt++)
        {
            await Task.Delay(retryDelays[attempt], cancellationToken);
            result = await ReleaseAsync(session, cancellationToken);
        }

        return result;
    }

    public async Task<SimulationSessionReleaseResult> ReleaseAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        var result = await gameServiceClient.ReleaseSimulationSessionAsync(
            session.SimulationSessionId,
            session.WorkerRuntimeId,
            session.SimulationSessionToken,
            cancellationToken);

        if (result.Succeeded
            || result.StatusCode is (int)HttpStatusCode.Unauthorized
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.Conflict)
        {
            var removed = sessionStore.Remove(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken);
            if (removed)
            {
                characterStores.Remove(
                    CharacterSessionRemovals.ConfirmedRelease,
                    session.CharacterId,
                    session.SimulationSessionId);
            }

            return SimulationSessionReleaseResult.Success();
        }

        return SimulationSessionReleaseResult.Failure(
            result.Error!.Code,
            result.Error.Message);
    }
}

public sealed record SimulationSessionReleaseResult(bool Succeeded, string? Code, string? Message)
{
    public static SimulationSessionReleaseResult Success()
    {
        return new SimulationSessionReleaseResult(true, null, null);
    }

    public static SimulationSessionReleaseResult Failure(string code, string message)
    {
        return new SimulationSessionReleaseResult(false, code, message);
    }
}
