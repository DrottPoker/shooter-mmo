using System.Net;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;

namespace Mmorpg.SimulationWorker.Sessions;

public sealed class SimulationSessionReleaseService(
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    CarryStateStore carryStateStore,
    IReadOnlyList<TimeSpan>? detachedReleaseRetryDelays = null)
{
    private static readonly TimeSpan[] DefaultDetachedReleaseRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    private readonly IReadOnlyList<TimeSpan> retryDelays =
        detachedReleaseRetryDelays ?? DefaultDetachedReleaseRetryDelays;

    // Releases a session that no connected peer owns any more. The local lease is dropped
    // first, so the heartbeat stops renewing it even when GameService stays unavailable
    // and the database lease then expires on its own.
    public async Task<SimulationSessionReleaseResult> ReleaseDetachedAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        if (sessionStore.Remove(
                session.CharacterId,
                session.SimulationSessionId,
                session.SimulationSessionToken))
        {
            carryStateStore.Remove(session.CharacterId, session.SimulationSessionId);
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
                carryStateStore.Remove(
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
