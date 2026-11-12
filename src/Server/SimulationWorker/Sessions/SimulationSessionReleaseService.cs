using System.Net;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Sessions;

// Releases simulation sessions through GameService, each with the final state of its character
// when the realtime loop captured one, and removes the state the worker kept for them from the
// registered per-character stores.
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

    // The final states that releases send, which the realtime loop captures. A release keeps one
    // until GameService answered it for good, so a retry sends it again.
    public CharacterFinalStateStore FinalStates { get; } = new();

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

        long? sentSequence = null;
        try
        {
            SimulationSessionReleaseResult result;
            (result, sentSequence) = await ReleaseCoreAsync(session, cancellationToken);
            for (var attempt = 0; !result.Succeeded && attempt < retryDelays.Count; attempt++)
            {
                await Task.Delay(retryDelays[attempt], cancellationToken);
                (result, sentSequence) = await ReleaseCoreAsync(session, cancellationToken);
            }

            return result;
        }
        finally
        {
            // No later attempt sends it; a newer state that the loop captured since stays.
            if (sentSequence is { } sequence)
            {
                FinalStates.Remove(session.CharacterId, session.SimulationSessionId, sequence);
            }
        }
    }

    public async Task<SimulationSessionReleaseResult> ReleaseAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        var (result, _) = await ReleaseCoreAsync(session, cancellationToken);
        return result;
    }

    // Sends the final state that the store holds for the session now, and forgets it once
    // GameService answered for good. Returns the sequence of the state it sent.
    private async Task<(SimulationSessionReleaseResult Result, long? SentSequence)> ReleaseCoreAsync(
        ActiveSimulationSession session,
        CancellationToken cancellationToken)
    {
        SimulationCharacterStateRequest? finalState = null;
        if (FinalStates.TryGet(session.CharacterId, session.SimulationSessionId, out var captured))
        {
            var state = captured.State;
            finalState = new SimulationCharacterStateRequest(
                captured.Sequence,
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.YawDegrees,
                state.Health,
                state.Mana);
        }

        var result = await gameServiceClient.ReleaseSimulationSessionAsync(
            session.SimulationSessionId,
            session.WorkerRuntimeId,
            session.SimulationSessionToken,
            finalState,
            cancellationToken);

        if (result.Succeeded
            || result.StatusCode is (int)HttpStatusCode.Unauthorized
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.Conflict)
        {
            if (finalState is not null)
            {
                FinalStates.Remove(session.CharacterId, session.SimulationSessionId, finalState.Sequence);
            }

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

            return (SimulationSessionReleaseResult.Success(), finalState?.Sequence);
        }

        return (
            SimulationSessionReleaseResult.Failure(result.Error!.Code, result.Error.Message),
            finalState?.Sequence);
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
