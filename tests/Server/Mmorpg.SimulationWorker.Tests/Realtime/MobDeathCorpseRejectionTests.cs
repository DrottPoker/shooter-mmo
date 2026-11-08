using Mmorpg.SimulationWorker.Realtime;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A Mob whose corpse GameService can never create leaves without it, so a dead Mob never stays in
// the world retrying forever; every other answer retries the corpse.
public sealed class MobDeathCorpseRejectionTests
{
    [Theory]
    [InlineData("mob_corpse_source_invalid", true)]
    [InlineData("mob_corpse_authority_invalid", true)]
    [InlineData("mob_corpse_persistence_invalid", true)]
    [InlineData("mob_corpse_invalid", true)]
    [InlineData("item_simulation_operation_invalid", true)]
    [InlineData("item_transaction_timeout", false)]
    [InlineData("auth_service_unavailable", false)]
    [InlineData("worker_runtime_changed", false)]
    public void OnlyARejectionThatCanNeverPassIsFinal(string code, bool final)
    {
        Assert.Equal(final, MobDeathProcessor.IsFinalCorpseRejection(code));
    }
}
