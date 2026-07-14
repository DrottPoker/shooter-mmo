using SimulationWorker.Auth;
using SimulationWorker.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

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
            "local-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            Guid.NewGuid(),
            "session-token",
            DateTime.UtcNow.AddMinutes(1),
            false)
        {
            IsSyntheticBot = true
        };

        var session = ActiveSimulationSession.FromJoinTicket(ticket);
        var response = ActiveSimulationSessionResponse.FromSession(session);

        Assert.True(session.IsSyntheticBot);
        Assert.True(response.IsSyntheticBot);
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
            "local-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            DateTime.UtcNow,
            sessionExpiresAt ?? DateTime.UtcNow.AddMinutes(1),
            false);
    }
}
