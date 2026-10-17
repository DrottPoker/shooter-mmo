using Mmorpg.Server.Common.Identity;

namespace Mmorpg.Server.Common.Tests;

public sealed class ScopedGuidTests
{
    // Derived ids are persisted by GameService and compared by SimulationWorker, so these
    // values must never change.
    [Theory]
    [InlineData("mob-loot-item", "ead33493-4012-5579-a0e2-34ea54f1c5fc")]
    [InlineData("mob-corpse-expiry", "8d241c7e-e185-5e28-bb4d-fc1fc845d277")]
    public void DerivationKeepsItsPersistedValues(string scope, string expected)
    {
        Assert.Equal(
            Guid.Parse(expected),
            ScopedGuid.Create(scope, Guid.Parse("c0000000-0000-4000-8000-000000000001")));
    }
}
