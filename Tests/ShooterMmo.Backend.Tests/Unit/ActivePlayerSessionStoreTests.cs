using WorldServer.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ActivePlayerSessionStoreTests
{
    [Fact]
    public void RegisterRejectsADifferentActiveSessionForTheSameCharacter()
    {
        var characterId = Guid.NewGuid();
        var first = CreateSession(characterId: characterId);
        var second = CreateSession(characterId: characterId);
        var store = new ActivePlayerSessionStore();

        Assert.Equal(ActivePlayerSessionRegistration.Joined, store.Register(first));
        Assert.Equal(ActivePlayerSessionRegistration.Conflict, store.Register(second));
        Assert.Single(store.List());
        Assert.Equal(first.WorldSessionId, store.List().Single().WorldSessionId);
    }

    [Fact]
    public void RegisterUpdatesAnExistingReconnectSession()
    {
        var worldSessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var first = CreateSession(
            worldSessionId: worldSessionId,
            sessionToken: "first-token",
            characterId: characterId);
        var reconnect = CreateSession(
            worldSessionId: worldSessionId,
            sessionToken: "rotated-token",
            characterId: characterId);
        var store = new ActivePlayerSessionStore();

        Assert.Equal(ActivePlayerSessionRegistration.Joined, store.Register(first));
        Assert.Equal(ActivePlayerSessionRegistration.Reconnected, store.Register(reconnect));
        Assert.True(store.TryGet(first.CharacterId, out var stored));
        Assert.Equal("rotated-token", stored!.WorldSessionToken);
        Assert.Equal(first.JoinedAt, stored.JoinedAt);
        Assert.True(stored.IsReconnect);
        Assert.False(store.Remove(first.CharacterId, worldSessionId, "first-token"));
        Assert.False(store.Refresh(
            first.CharacterId,
            worldSessionId,
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
        var store = new ActivePlayerSessionStore();

        Assert.Equal(ActivePlayerSessionRegistration.Joined, store.Register(expired));
        Assert.Equal(ActivePlayerSessionRegistration.ReplacedExpired, store.Register(replacement));
        Assert.Equal(replacement.WorldSessionId, store.List().Single().WorldSessionId);
    }

    [Fact]
    public void RefreshAndRemoveRequireTheMatchingWorldSessionId()
    {
        var session = CreateSession();
        var store = new ActivePlayerSessionStore();
        store.Register(session);
        var refreshedExpiry = DateTime.UtcNow.AddMinutes(1);

        Assert.False(store.Refresh(
            session.CharacterId,
            Guid.NewGuid(),
            session.WorldSessionToken,
            refreshedExpiry));
        Assert.True(store.Refresh(
            session.CharacterId,
            session.WorldSessionId,
            session.WorldSessionToken,
            refreshedExpiry));
        Assert.Equal(refreshedExpiry, store.List().Single().SessionExpiresAt);
        Assert.False(store.Remove(
            session.CharacterId,
            Guid.NewGuid(),
            session.WorldSessionToken));
        Assert.True(store.Remove(
            session.CharacterId,
            session.WorldSessionId,
            session.WorldSessionToken));
        Assert.Empty(store.List());
    }

    [Fact]
    public void InvalidationRemovesTheExactSessionAndPreservesItsDisconnectReason()
    {
        var session = CreateSession();
        var store = new ActivePlayerSessionStore();
        store.Register(session);

        Assert.False(store.Invalidate(
            session.CharacterId,
            session.WorldSessionId,
            "wrong-token",
            "account_session_replaced",
            "This account logged in from another client."));
        Assert.True(store.Invalidate(
            session.CharacterId,
            session.WorldSessionId,
            session.WorldSessionToken,
            "account_session_replaced",
            "This account logged in from another client."));

        Assert.Empty(store.ListActiveSessions());
        Assert.True(store.TryTakeInvalidation(session.WorldSessionId, out var invalidation));
        Assert.Equal("account_session_replaced", invalidation!.Code);
        Assert.Equal("This account logged in from another client.", invalidation.Message);
        Assert.False(store.TryTakeInvalidation(session.WorldSessionId, out _));
    }

    private static ActivePlayerSession CreateSession(
        Guid? worldSessionId = null,
        string sessionToken = "session-token",
        Guid? characterId = null,
        DateTime? sessionExpiresAt = null)
    {
        return new ActivePlayerSession(
            worldSessionId ?? Guid.NewGuid(),
            sessionToken,
            Guid.NewGuid(),
            characterId ?? Guid.NewGuid(),
            "Hero One",
            "local-world-1",
            DateTime.UtcNow,
            sessionExpiresAt ?? DateTime.UtcNow.AddMinutes(1),
            false);
    }
}
