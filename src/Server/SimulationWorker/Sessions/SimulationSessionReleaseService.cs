using System.Net;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Sessions;

// Releases simulation sessions through GameService, each with the final state of its character
// when the realtime loop captured one, and removes the state the worker kept for them from the
// registered per-character stores.
public sealed class SimulationSessionReleaseService(
    GameServiceClient gameServiceClient,
    ActiveSimulationSessionStore sessionStore,
    CharacterSessionStores characterStores,
    IReadOnlyList<TimeSpan>? detachedReleaseRetryDelays = null,
    GameServiceAvailability? availability = null,
    SimulationWorkerConfig? config = null,
    TimeProvider? timeProvider = null)
{
    // How long a final state stays for a reconnect's join after its release was refused: as long
    // as the session's lease can last without this worker renewing it. After that no join can
    // take the session over any more.
    public static readonly TimeSpan DefaultReconnectStateLifetime = TimeSpan.FromSeconds(
        SimulationWorkerConfig.DefaultGameServiceOutageToleranceSeconds + 30);

    private readonly TimeSpan reconnectStateLifetime = config?.MinimumSessionLease ?? DefaultReconnectStateLifetime;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

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
    // until GameService answered it for good, so a retry sends it again. A release whose
    // credentials GameService refused keeps it longer: a reconnect rotated the session's token,
    // so the session goes on, and the reconnect's join places its new entity from the state. It
    // stays for the reconnect state lifetime at most, and every release forgets the states whose
    // time ran out, so a reconnect that never completes leaves nothing behind for good.
    public CharacterFinalStateStore FinalStates { get; } = new();

    // Releases a session that no connected peer owns any more. The local lease is dropped
    // first, so the heartbeat stops renewing it even when GameService stays unavailable
    // and the database lease then expires on its own. The state the join kept for the session
    // goes with it. While GameService does not answer, each attempt waits for it instead of
    // spending a timeout, and goes as soon as it answers again; the session lease outlasts that
    // wait. A worker that stops first gives the release up.
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

        long? doneSequence = null;
        try
        {
            if (!await WaitForGameServiceAsync(cancellationToken))
            {
                return Unavailable();
            }

            SimulationSessionReleaseResult result;
            (result, doneSequence) = await ReleaseCoreAsync(session, cancellationToken);
            for (var attempt = 0; !result.Succeeded && attempt < retryDelays.Count; attempt++)
            {
                await Task.Delay(retryDelays[attempt], cancellationToken);
                if (!await WaitForGameServiceAsync(cancellationToken))
                {
                    return result;
                }

                (result, doneSequence) = await ReleaseCoreAsync(session, cancellationToken);
            }

            return result;
        }
        finally
        {
            // No later attempt sends it; a newer state that the loop captured since stays.
            if (doneSequence is { } sequence)
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

    private Task<bool> WaitForGameServiceAsync(CancellationToken cancellationToken)
    {
        return availability is null
            ? Task.FromResult(true)
            : availability.WaitUntilAvailableAsync(cancellationToken);
    }

    private static SimulationSessionReleaseResult Unavailable()
    {
        return SimulationSessionReleaseResult.Failure(
            GameServiceAvailability.UnavailableCode,
            GameServiceAvailability.UnavailableMessage);
    }

    // Sends the final state that the store holds for the session now, and forgets it once
    // GameService answered for good, unless GameService refused the credentials. Returns the
    // sequence of the state that no later attempt needs, or null when none was sent or it stays.
    private async Task<(SimulationSessionReleaseResult Result, long? DoneSequence)> ReleaseCoreAsync(
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

        FinalStates.RemoveExpired(clock.GetUtcNow().UtcDateTime);
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
            // GameService refuses the token of a session whose reconnect consumed its ticket, which
            // rotated the token there before the join told this worker. Such a release ended
            // nothing and saved nothing, and the session goes on, so its state stays for the
            // reconnect's join.
            var refusedCredentials = result.StatusCode == (int)HttpStatusCode.Unauthorized;
            if (finalState is not null && !refusedCredentials)
            {
                FinalStates.Remove(session.CharacterId, session.SimulationSessionId, finalState.Sequence);
            }
            else if (finalState is not null)
            {
                FinalStates.KeepUntil(
                    session.CharacterId,
                    session.SimulationSessionId,
                    finalState.Sequence,
                    clock.GetUtcNow().UtcDateTime + reconnectStateLifetime);
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

            return (SimulationSessionReleaseResult.Success(), refusedCredentials ? null : finalState?.Sequence);
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
