using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class ActiveSimulationSessionStoreTests
{
    [Fact]
    public void SyntheticBotClassificationFlowsFromTicketToSessionResponse()
    {
        var ticket = new ConsumedSimulationJoinTicketResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Active Bot 1",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            Guid.NewGuid(),
            "session-token",
            DateTime.UtcNow.AddMinutes(1),
            0,
            0,
            200,
            false)
        {
            IsSyntheticBot = true,
            SessionLeaseDurationMilliseconds = 30_000
        };

        var session = ActiveSimulationSession.FromJoinTicket(ticket, DateTime.UtcNow);
        var response = ActiveSimulationSessionResponse.FromSession(session);

        Assert.True(session.IsSyntheticBot);
        Assert.True(response.IsSyntheticBot);
    }

    [Fact]
    public void SessionDeadlineUsesTheWorkerClockAndTheLeaseDuration()
    {
        // The database clock is far ahead of the worker clock.
        var ticket = new ConsumedSimulationJoinTicketResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Clock Skew Hero",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            Guid.NewGuid(),
            "session-token",
            DateTime.UtcNow.AddHours(5),
            0,
            0,
            200,
            false)
        {
            SessionLeaseDurationMilliseconds = 30_000
        };
        var requestedAt = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        var session = ActiveSimulationSession.FromJoinTicket(ticket, requestedAt);

        Assert.Equal(requestedAt.AddSeconds(30), session.SessionExpiresAt);
    }

    [Fact]
    public void RegisterRejectsADifferentActiveSessionForTheSameCharacter()
    {
        var characterId = Guid.NewGuid();
        var first = CreateSession(characterId: characterId);
        var second = CreateSession(characterId: characterId);
        var store = new ActiveSimulationSessionStore();

        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(first));
        Assert.Equal(ActiveSimulationSessionRegistration.Conflict, store.Register(second));
        Assert.Single(store.List());
        Assert.Equal(first.SimulationSessionId, store.List().Single().SimulationSessionId);
    }

    [Fact]
    public void RegisterUpdatesAnExistingReconnectSession()
    {
        var simulationSessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var first = CreateSession(
            simulationSessionId: simulationSessionId,
            sessionToken: "first-token",
            characterId: characterId);
        var reconnect = CreateSession(
            simulationSessionId: simulationSessionId,
            sessionToken: "rotated-token",
            characterId: characterId);
        var store = new ActiveSimulationSessionStore();

        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(first));
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(reconnect));
        Assert.True(store.TryGet(first.CharacterId, out var stored));
        Assert.Equal("rotated-token", stored!.SimulationSessionToken);
        Assert.Equal(first.JoinedAt, stored.JoinedAt);
        Assert.True(stored.IsReconnect);
        Assert.False(store.Remove(first.CharacterId, simulationSessionId, "first-token"));
        Assert.False(store.Refresh(
            first.CharacterId,
            simulationSessionId,
            "first-token",
            DateTime.UtcNow.AddMinutes(2)));
        Assert.Single(store.List());
    }

    [Fact]
    public void RegisterReplacesAnExpiredLocalSession()
    {
        var characterId = Guid.NewGuid();
        var expired = CreateSession(
            characterId: characterId,
            sessionExpiresAt: DateTime.UtcNow.AddSeconds(-1));
        var replacement = CreateSession(characterId: characterId);
        var store = new ActiveSimulationSessionStore();

        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(expired));
        Assert.Equal(ActiveSimulationSessionRegistration.ReplacedExpired, store.Register(replacement));
        Assert.Equal(replacement.SimulationSessionId, store.List().Single().SimulationSessionId);
    }

    [Fact]
    public void RefreshAndRemoveRequireTheMatchingSimulationSessionId()
    {
        var session = CreateSession();
        var store = new ActiveSimulationSessionStore();
        store.Register(session);
        var refreshedExpiry = DateTime.UtcNow.AddMinutes(1);

        Assert.False(store.Refresh(
            session.CharacterId,
            Guid.NewGuid(),
            session.SimulationSessionToken,
            refreshedExpiry));
        Assert.True(store.Refresh(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            refreshedExpiry));
        Assert.Equal(refreshedExpiry, store.List().Single().SessionExpiresAt);
        Assert.False(store.Remove(
            session.CharacterId,
            Guid.NewGuid(),
            session.SimulationSessionToken));
        Assert.True(store.Remove(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken));
        Assert.Empty(store.List());
    }

    [Fact]
    public void InvalidationRemovesTheExactSessionAndPreservesItsDisconnectReason()
    {
        var session = CreateSession();
        var store = new ActiveSimulationSessionStore();
        store.Register(session);

        Assert.False(store.Invalidate(
            session.CharacterId,
            session.SimulationSessionId,
            "wrong-token",
            "account_session_replaced",
            "This account logged in from another client."));
        Assert.True(store.Invalidate(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            "account_session_replaced",
            "This account logged in from another client."));

        Assert.Empty(store.ListActiveSessions());
        Assert.True(store.TryTakeInvalidation(session.SimulationSessionId, out var invalidation));
        Assert.Equal("account_session_replaced", invalidation!.Code);
        Assert.Equal("This account logged in from another client.", invalidation.Message);
        Assert.False(store.TryTakeInvalidation(session.SimulationSessionId, out _));
    }

    [Fact]
    public void IsCurrentExpiresTheExactSessionAndPreservesItsDisconnectReason()
    {
        var session = CreateSession(sessionExpiresAt: DateTime.UtcNow.AddSeconds(1));
        var store = new ActiveSimulationSessionStore();
        store.Register(session);

        Assert.True(store.IsCurrent(session, session.SessionExpiresAt.AddTicks(-1)));
        Assert.False(store.IsCurrent(session, session.SessionExpiresAt));
        Assert.Empty(store.ListActiveSessions());
        Assert.True(store.TryTakeInvalidation(session.SimulationSessionId, out var invalidation));
        Assert.Equal("session_expired", invalidation!.Code);
        Assert.Equal("The simulation session lease expired.", invalidation.Message);
    }

    [Fact]
    public void InvalidationsOfDetachedSessionsArePrunedAfterTheRetention()
    {
        var store = new ActiveSimulationSessionStore();
        var detached = CreateSession();
        store.Register(detached);
        Assert.True(store.Invalidate(
            detached.CharacterId,
            detached.SimulationSessionId,
            detached.SimulationSessionToken,
            "session_revoked",
            "No peer owns this session any more."));
        Assert.Equal(1, store.PendingInvalidationCount);

        Assert.Equal(0, store.PruneInvalidations(DateTime.UtcNow - ActiveSimulationSessionStore.InvalidationRetention));
        Assert.Equal(1, store.PendingInvalidationCount);
        Assert.Equal(1, store.PruneInvalidations(DateTime.UtcNow.AddSeconds(1)));
        Assert.Equal(0, store.PendingInvalidationCount);
        Assert.False(store.TryTakeInvalidation(detached.SimulationSessionId, out _));
    }

    // The end of an account session marks the current generation and keeps the session current; an
    // earlier generation is not marked, and a reconnect, a removal, or an invalidation forgets it.
    [Fact]
    public void AnEndedAccountSessionMarksOnlyTheCurrentGenerationAndKeepsItCurrent()
    {
        var store = new ActiveSimulationSessionStore();
        var session = CreateSession();
        store.Register(session);
        Assert.False(store.TryGetAccountSessionEnd(session, out _));

        Assert.False(store.MarkAccountSessionEnded(session.CharacterId, session.SimulationSessionId, "older-token", "account_session_ended"));
        Assert.True(store.MarkAccountSessionEnded(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken, "account_session_replaced"));
        Assert.False(store.MarkAccountSessionEnded(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken, "account_session_ended"));

        Assert.True(store.TryGetAccountSessionEnd(session, out var code));
        Assert.Equal("account_session_replaced", code);
        Assert.True(store.IsCurrent(session, DateTime.UtcNow));
        Assert.False(store.TryTakeInvalidation(session.SimulationSessionId, out _));
        Assert.False(store.TryGetAccountSessionEnd(session with { SimulationSessionToken = "older-token" }, out _));

        var reconnected = session with { SimulationSessionToken = "newer-token" };
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(reconnected));
        Assert.False(store.TryGetAccountSessionEnd(reconnected, out _));

        Assert.True(store.MarkAccountSessionEnded(reconnected.CharacterId, reconnected.SimulationSessionId, reconnected.SimulationSessionToken, "account_session_ended"));
        Assert.True(store.Remove(reconnected.CharacterId, reconnected.SimulationSessionId, reconnected.SimulationSessionToken));
        Assert.False(store.TryGetAccountSessionEnd(reconnected, out _));

        var invalidated = CreateSession();
        store.Register(invalidated);
        Assert.True(store.MarkAccountSessionEnded(invalidated.CharacterId, invalidated.SimulationSessionId, invalidated.SimulationSessionToken, "account_session_ended"));
        Assert.True(store.Invalidate(invalidated.CharacterId, invalidated.SimulationSessionId, invalidated.SimulationSessionToken, "session_revoked", "Ended."));
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(invalidated));
        Assert.False(store.TryGetAccountSessionEnd(invalidated, out _));
    }

    // Without a join in flight, a refusal of the session's credentials ends the session at once;
    // a refusal of a token that a reconnect already replaced ends nothing.
    [Fact]
    public void ARefusalOfTheCredentialsEndsTheSessionWhileNoJoinIsInFlight()
    {
        var store = new ActiveSimulationSessionStore();
        var session = CreateSession();
        var reconnected = session with { SimulationSessionToken = "reconnected-token" };
        store.Register(session);
        var ended = 0;

        Assert.Equal(
            SessionCredentialsRefusal.Ended,
            store.RefuseCredentials(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken, "session_revoked", "Refused.", () => ended++));

        Assert.Equal(1, ended);
        Assert.False(store.IsCurrentGeneration(session));
        Assert.True(store.TryTakeInvalidation(session.SimulationSessionId, out var invalidation));
        Assert.Equal("session_revoked", invalidation!.Code);
        store.Register(reconnected);
        Assert.Equal(
            SessionCredentialsRefusal.EarlierGeneration,
            store.RefuseCredentials(session.CharacterId, session.SimulationSessionId, session.SimulationSessionToken, "session_revoked", "Refused.", () => ended++));
        Assert.True(store.IsCurrentGeneration(reconnected));
        Assert.Equal(1, ended);
    }

    // A reconnect's join rotates the token at GameService before it registers the new one, so a
    // refusal that comes while joins are in flight waits for every one of them. The session goes
    // on when one of them registered a newer generation of it, and ends once the last of them
    // ended without one; a join that began after the refusal does not hold it.
    [Fact]
    public void ARefusalWaitsForTheJoinsInFlightAndEndsNothingWhenOneRegisteredTheSessionAgain()
    {
        var store = new ActiveSimulationSessionStore();
        var reconnecting = CreateSession();
        var ending = CreateSession();
        store.Register(reconnecting);
        store.Register(ending);
        var endedSessions = new List<Guid>();
        var first = store.BeginJoin();
        var second = store.BeginJoin();

        foreach (var session in new[] { reconnecting, ending })
        {
            Assert.Equal(
                SessionCredentialsRefusal.WaitsForJoins,
                store.RefuseCredentials(
                    session.CharacterId,
                    session.SimulationSessionId,
                    session.SimulationSessionToken,
                    "session_revoked",
                    "Refused.",
                    () => endedSessions.Add(session.SimulationSessionId)));
        }

        var later = store.BeginJoin();
        Assert.True(store.IsCurrent(reconnecting, DateTime.UtcNow));
        Assert.True(store.IsCurrent(ending, DateTime.UtcNow));

        var reconnected = reconnecting with { SimulationSessionToken = "reconnected-token" };
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(reconnected));
        store.EndJoin(first);
        Assert.Empty(endedSessions);
        Assert.True(store.IsCurrent(ending, DateTime.UtcNow));

        store.EndJoin(second);
        Assert.Equal([ending.SimulationSessionId], endedSessions);
        Assert.False(store.IsCurrentGeneration(ending));
        Assert.True(store.TryTakeInvalidation(ending.SimulationSessionId, out _));
        Assert.True(store.IsCurrent(reconnected, DateTime.UtcNow));
        Assert.False(store.TryTakeInvalidation(reconnecting.SimulationSessionId, out _));

        store.EndJoin(later);
        store.EndJoin(later);
        Assert.Single(endedSessions);
    }

    private static ActiveSimulationSession CreateSession(
        Guid? simulationSessionId = null,
        string sessionToken = "session-token",
        Guid? characterId = null,
        DateTime? sessionExpiresAt = null)
    {
        return new ActiveSimulationSession(
            simulationSessionId ?? Guid.NewGuid(),
            sessionToken,
            Guid.NewGuid(),
            characterId ?? Guid.NewGuid(),
            "Hero One",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            DateTime.UtcNow,
            sessionExpiresAt ?? DateTime.UtcNow.AddMinutes(1),
            PlayerCarryState.Default,
            false);
    }
}
