using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions.Handlers;

namespace Mmorpg.Backend.Tests.Unit;

// White-box tests of the command validation that handlers run before they lock anything.
public sealed class ItemCommandValidationTests
{
    [Fact]
    public void ValidPersistentMobCorpseCommandPasses()
    {
        MobCorpseCommandHandler.ValidatePersistentMobCorpseCommand(MobCorpse());
    }

    [Fact]
    public void InvalidPersistentMobCorpseCommandsAreRejected()
    {
        var grantId = Guid.NewGuid();
        AssertMobCorpseInvalid(MobCorpse() with { CorpseId = Guid.Empty });
        AssertMobCorpseInvalid(MobCorpse() with { WorkerId = "worker one" });
        AssertMobCorpseInvalid(MobCorpse() with { PresentationKey = "corpse.other" });
        AssertMobCorpseInvalid(MobCorpse() with { LifetimeSeconds = 0.5d });
        AssertMobCorpseInvalid(MobCorpse() with { LifetimeSeconds = double.NaN });
        AssertMobCorpseInvalid(MobCorpse() with { WorkerHeartbeatTimeoutSeconds = 0 });
        AssertMobCorpseInvalid(MobCorpse() with { PositionY = double.PositiveInfinity });
        AssertMobCorpseInvalid(MobCorpse() with { RotationW = 0.5d });
        AssertMobCorpseInvalid(MobCorpse() with { Loot = [] });
        AssertMobCorpseInvalid(MobCorpse() with
        {
            Loot =
            [
                new MobCorpseLootEntryRequest(grantId, "material.iron_ore", 1),
                new MobCorpseLootEntryRequest(grantId, "material.iron_ore", 2)
            ]
        });
        AssertMobCorpseInvalid(MobCorpse() with
        {
            Loot = [new MobCorpseLootEntryRequest(Guid.NewGuid(), "material.iron_ore", 0)]
        });
    }

    private static CreatePersistentMobCorpseCommand MobCorpse()
    {
        return new CreatePersistentMobCorpseCommand(
            Guid.NewGuid(),
            "worker-1",
            "runtime-1",
            "shard-1",
            "mob.feral_wolf",
            "Feral Wolf",
            1d,
            0d,
            -1d,
            0d,
            0d,
            0d,
            1d,
            "corpse.generic_loot_crate",
            60d,
            30,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), "material.iron_ore", 1)]);
    }

    private static void AssertMobCorpseInvalid(CreatePersistentMobCorpseCommand command)
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(
            () => MobCorpseCommandHandler.ValidatePersistentMobCorpseCommand(command));
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseInvalid, exception.Code);
        Assert.Equal("The persistent Mob corpse command is incomplete or invalid.", exception.Message);
    }
}
