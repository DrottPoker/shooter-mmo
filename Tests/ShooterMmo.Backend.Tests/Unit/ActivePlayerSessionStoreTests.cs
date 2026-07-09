using WorldServer.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ActivePlayerSessionStoreTests
{
    [Fact]
    public void StoreRejectsDuplicateCharacterSessionsAndSupportsRemoval()
    {
        var characterId = Guid.NewGuid();
        var session = new ActivePlayerSession(
            Guid.NewGuid(),
            characterId,
            "Hero One",
            "local-world-1",
            DateTime.UtcNow);

        var store = new ActivePlayerSessionStore();

        Assert.True(store.TryAdd(session));
        Assert.False(store.TryAdd(session));
        Assert.Single(store.List());
        Assert.True(store.Remove(characterId));
        Assert.False(store.Remove(characterId));
        Assert.Empty(store.List());
    }
}
