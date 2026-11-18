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

        var session = ActiveSimulationSession.FromJoinTicket(ticket, requestedAt, requestedTimestamp: 1_234);

        Assert.Equal(requestedAt.AddSeconds(30), session.SessionExpiresAt);
        Assert.Equal(new SessionLease(1_234, TimeSpan.FromSeconds(30)), session.Lease);
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
            store.GetTimestamp(),
            TimeSpan.FromMinutes(2)));
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
        var refreshedAt = store.GetTimestamp();

        Assert.False(store.Refresh(
            session.CharacterId,
            Guid.NewGuid(),
            session.SimulationSessionToken,
            refreshedAt,
            TimeSpan.FromMinutes(1)));
        Assert.True(store.Refresh(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            refreshedAt,
            TimeSpan.FromMinutes(1)));
        Assert.True(store.TryGet(session.CharacterId, out var refreshed));
        Assert.Equal(new SessionLease(refreshedAt, TimeSpan.FromMinutes(1)), refreshed!.Lease);
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
        var time = new SteppedTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var session = CreateSession(sessionExpiresAt: time.GetUtcNow().UtcDateTime.AddSeconds(1));
        var store = new ActiveSimulationSessionStore(time);
        store.Register(session);

        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        Assert.True(store.IsCurrent(session));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(store.IsCurrent(session));
        Assert.Empty(store.ListActiveSessions());
        Assert.True(store.TryTakeInvalidation(session.SimulationSessionId, out var invalidation));
        Assert.Equal("session_expired", invalidation!.Code);
        Assert.Equal("The simulation session lease expired.", invalidation.Message);
    }

    // The lease counts from the request's send time on the monotonic clock. A host that steps its
    // wall clock forward must not drop every session at once, and one that steps it back must not
    // extend a lease beyond the one GameService keeps.
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ALeaseDoesNotFollowTheWallClock(int stepHours)
    {
        var time = new SteppedTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var store = new ActiveSimulationSessionStore(time);
        var session = CreateSession() with { Lease = new SessionLease(store.GetTimestamp(), TimeSpan.FromSeconds(150)) };
        store.Register(session);
        time.Advance(TimeSpan.FromSeconds(100));

        time.StepWallClock(TimeSpan.FromHours(stepHours));
        Assert.True(store.IsCurrent(session));
        time.Advance(TimeSpan.FromSeconds(49));
        Assert.True(store.IsCurrent(session));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(store.IsCurrent(session));
        Assert.True(store.TryTakeInvalidation(session.SimulationSessionId, out var invalidation));
        Assert.Equal("session_expired", invalidation!.Code);
    }

    // A renewal counts from the timestamp of the request, not from the answer, and a step of the
    // wall clock between the two moves nothing; the time the client is told follows the lease.
    [Fact]
    public void ARenewalCountsFromTheSendTimestampWhateverTheWallClockDid()
    {
        var time = new SteppedTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var store = new ActiveSimulationSessionStore(time);
        var session = CreateSession() with { Lease = new SessionLease(store.GetTimestamp(), TimeSpan.FromSeconds(150)) };
        store.Register(session);
        time.Advance(TimeSpan.FromSeconds(100));
        var sentAt = store.GetTimestamp();
        time.Advance(TimeSpan.FromSeconds(5));
        time.StepWallClock(TimeSpan.FromHours(-3));

        Assert.True(store.Refresh(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            sentAt,
            TimeSpan.FromSeconds(150)));

        // 150 seconds from the send, 5 of which passed: 145 remain.
        var reported = store.List().Single().SessionExpiresAt;
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddSeconds(145), reported);
        time.Advance(TimeSpan.FromSeconds(144));
        Assert.True(store.IsCurrent(session));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(store.IsCurrent(session));
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
        Assert.True(store.IsCurrent(session));
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
        Assert.True(store.IsCurrent(reconnecting));
        Assert.True(store.IsCurrent(ending));

        var reconnected = reconnecting with { SimulationSessionToken = "reconnected-token" };
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(reconnected));
        store.EndJoin(first);
        Assert.Empty(endedSessions);
        Assert.True(store.IsCurrent(ending));

        store.EndJoin(second);
        Assert.Equal([ending.SimulationSessionId], endedSessions);
        Assert.False(store.IsCurrentGeneration(ending));
        Assert.True(store.TryTakeInvalidation(ending.SimulationSessionId, out _));
        Assert.True(store.IsCurrent(reconnected));
        Assert.False(store.TryTakeInvalidation(reconnecting.SimulationSessionId, out _));

        store.EndJoin(later);
        store.EndJoin(later);
        Assert.Single(endedSessions);
    }

    // Two joins of one session consume their tickets at GameService in one order and register
    // here in the other: the generation that GameService issued first is refused, and the newer
    // one stays untouched, whichever order the registrations come in.
    [Fact]
    public void ARegistrationOfAnOlderGenerationIsRefusedAndTheNewerOneStays()
    {
        var store = new ActiveSimulationSessionStore();
        var first = CreateSession(sessionToken: "token-1") with { Generation = 1 };
        var second = first with { SimulationSessionToken = "token-2", Generation = 2 };
        var third = first with { SimulationSessionToken = "token-3", Generation = 3 };

        // The losing order: the join that consumed last registers first.
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(second));
        Assert.Equal(ActiveSimulationSessionRegistration.StaleGeneration, store.Register(first));
        Assert.True(store.TryGet(first.CharacterId, out var held));
        Assert.Equal("token-2", held!.SimulationSessionToken);
        Assert.True(store.IsCurrent(second));
        Assert.False(store.IsCurrent(first));

        // The refused generation did not touch what the held one carries.
        Assert.False(store.MarkAccountSessionEnded(first.CharacterId, first.SimulationSessionId, "token-1", "account_session_ended"));
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(third));
        Assert.Equal(ActiveSimulationSessionRegistration.StaleGeneration, store.Register(second));
        Assert.True(store.TryGet(first.CharacterId, out held));
        Assert.Equal("token-3", held!.SimulationSessionToken);

        // The order they consumed in: both register, the later one wins.
        var other = new ActiveSimulationSessionStore();
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, other.Register(first));
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, other.Register(second));
        Assert.True(other.TryGet(first.CharacterId, out held));
        Assert.Equal("token-2", held!.SimulationSessionToken);
    }

    // A generation of 0 is one that GameService did not number, such as a development bot's: it
    // is never refused and never refuses, as before generations existed.
    [Fact]
    public void AnUnnumberedGenerationIsNeverOrdered()
    {
        var store = new ActiveSimulationSessionStore();
        var numbered = CreateSession(sessionToken: "token-1") with { Generation = 5 };
        var unnumbered = numbered with { SimulationSessionToken = "token-2", Generation = 0 };
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, store.Register(numbered));

        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(unnumbered));
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(numbered with { SimulationSessionToken = "token-3", Generation = 1 }));
        Assert.True(store.TryGet(numbered.CharacterId, out var held));
        Assert.Equal("token-3", held!.SimulationSessionToken);
    }

    // The per-session state goes with the generation that ends, never with a newer one that
    // registered meanwhile; the check and the removal are one step against Register.
    [Fact]
    public void StateOfASessionIsRemovedOnlyWhileNoNewerGenerationHoldsIt()
    {
        var store = new ActiveSimulationSessionStore();
        var older = CreateSession(sessionToken: "token-1") with { Generation = 1 };
        var newer = older with { SimulationSessionToken = "token-2", Generation = 2 };
        store.Register(older);
        var removed = 0;

        Assert.True(store.RunUnlessSuperseded(older, () => removed++));
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, store.Register(newer));
        Assert.False(store.RunUnlessSuperseded(older, () => removed++));
        Assert.True(store.RunUnlessSuperseded(newer, () => removed++));
        Assert.True(store.RunUnlessSuperseded(CreateSession(), () => removed++));

        Assert.Equal(3, removed);
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
