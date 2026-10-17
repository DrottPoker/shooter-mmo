using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Items.Transactions.Handlers;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

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
                new MobCorpseLootEntryRequest(grantId, "fixture.ore", 1),
                new MobCorpseLootEntryRequest(grantId, "fixture.ore", 2)
            ]
        });
        AssertMobCorpseInvalid(MobCorpse() with
        {
            Loot = [new MobCorpseLootEntryRequest(Guid.NewGuid(), "fixture.ore", 0)]
        });
    }

    [Fact]
    public void PlayerDeathCommandValidationRejectsWithStableCodes()
    {
        PlayerDeathCommandHandler.ValidatePlayerDeathCommand(Death());
        AssertDeathRejected(
            Death() with { DeathEventId = Guid.Empty },
            ItemTransactionErrorCodes.DeathEventConflict,
            "A death event and character id are required.");
        AssertDeathRejected(
            Death() with { ShardId = "shard one" },
            ItemTransactionErrorCodes.SimulationSessionInvalid,
            "The death event Shard id is invalid.");
        AssertDeathRejected(
            Death() with { PresentationKey = "corpse.other_crate" },
            ItemTransactionErrorCodes.DeathEventConflict,
            "Player death must use the authoritative generic corpse presentation key.");
        AssertDeathRejected(
            Death() with { PositionZ = 2_000_000d },
            ItemTransactionErrorCodes.DeathEventConflict,
            "The death transform position is invalid.");
        AssertDeathRejected(
            Death() with { RotationW = 0.9d },
            ItemTransactionErrorCodes.DeathEventConflict,
            "The death transform rotation must be a normalized quaternion.");
    }

    [Fact]
    public void DeathEventRequestHashIsTheStoredFingerprint()
    {
        var command = Death() with
        {
            DeathEventId = Guid.Parse("d0000000-0000-4000-8000-000000000001"),
            CharacterId = Guid.Parse("d0000000-0000-4000-8000-000000000002")
        };
        const string fingerprint =
            """{"deathEventId":"d0000000-0000-4000-8000-000000000001","characterId":"d0000000-0000-4000-8000-000000000002","shardId":"shard-1","positionX":1.5,"positionY":0,"positionZ":-2.25,"rotationX":0,"rotationY":0,"rotationZ":0,"rotationW":1,"presentationKey":"corpse.remains"}""";

        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fingerprint))),
            PlayerDeathCommandHandler.CalculateDeathEventRequestHash(command));
    }

    [Fact]
    public void InvalidCorpseItemTransferKeepsItsWireCode()
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(
            () => CorpseItemTransfers.ValidateCorpseItemTransferCommand(new CorpseItemTransferCommand(
                Guid.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                null,
                Guid.NewGuid(),
                0,
                0,
                null,
                null)));

        Assert.Equal("item_simulation_operation_invalid", exception.Code);
        Assert.Equal("The corpse loot command is incomplete or invalid.", exception.Message);
    }

    private static ProcessPlayerDeathCommand Death()
    {
        return new ProcessPlayerDeathCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            "shard-1",
            1.5d,
            0d,
            -2.25d,
            0d,
            0d,
            0d,
            1d,
            "corpse.remains");
    }

    private static void AssertDeathRejected(ProcessPlayerDeathCommand command, string code, string message)
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(
            () => PlayerDeathCommandHandler.ValidatePlayerDeathCommand(command));
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
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
            "corpse.remains",
            60d,
            30,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), "fixture.ore", 1)]);
    }

    private static void AssertMobCorpseInvalid(CreatePersistentMobCorpseCommand command)
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(
            () => MobCorpseCommandHandler.ValidatePersistentMobCorpseCommand(command));
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseInvalid, exception.Code);
        Assert.Equal("The persistent Mob corpse command is incomplete or invalid.", exception.Message);
    }
}
