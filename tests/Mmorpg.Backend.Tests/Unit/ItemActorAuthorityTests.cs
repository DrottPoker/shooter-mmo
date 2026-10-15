using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.Backend.Tests.Unit;

// The actor validation matrix that the kernel runs before live authority and the claim,
// and the live container access rules for city services.
public sealed class ItemActorAuthorityTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public void CompleteActorsAreValid()
    {
        ItemActorAuthority.EnsureValid(ItemTransactionActor.ForAccount(AccountId));
        ItemActorAuthority.EnsureValid(ItemTransactionActor.ForOfflineAccount(AccountId));
        ItemActorAuthority.EnsureValid(ItemTransactionActor.ForSystem());
        ItemActorAuthority.EnsureValid(Simulation());
    }

    [Fact]
    public void IncompleteOrMixedActorsAreRejected()
    {
        AssertInvalid(
            new ItemTransactionActor(ItemTransactionAuthority.Account, Guid.Empty),
            "An account-authorized item operation requires an account id.");
        AssertInvalid(
            ItemTransactionActor.ForAccount(AccountId) with { Simulation = Simulation().Simulation },
            "An account-authorized item operation cannot supply simulation authority.");
        AssertInvalid(
            ItemTransactionActor.ForSystem() with { AccountId = AccountId },
            "A system item operation cannot impersonate account or simulation authority.");
        AssertInvalid(
            ItemTransactionActor.ForSystem() with { RequiresOfflineCharacter = true },
            "A system item operation cannot impersonate account or simulation authority.");
        AssertInvalid(
            Simulation() with { RequiresOfflineCharacter = true },
            "A simulation-authorized item operation requires complete live authority.");
        AssertInvalid(
            Simulation() with { Simulation = Simulation().Simulation! with { WorkerId = "worker one" } },
            "A simulation-authorized item operation requires complete live authority.");
        AssertInvalid(
            Simulation() with { Simulation = Simulation().Simulation! with { SessionTokenHash = " " } },
            "A simulation-authorized item operation requires complete live authority.");
        AssertInvalid(
            Simulation() with { Simulation = null },
            "A simulation-authorized item operation requires complete live authority.");
        AssertInvalid(
            new ItemTransactionActor((ItemTransactionAuthority)99, AccountId),
            "The item operation authority is invalid.");
    }

    [Fact]
    public void CityServicesRequireValidatedLiveAccess()
    {
        new ItemActorAuthority(ItemTransactionActor.ForSystem()).EnsureLiveContainerAccess("bank");
        new ItemActorAuthority(Simulation(new ItemTransactionLiveAccess(true, true, false)))
            .EnsureLiveContainerAccess("recovery_storage");
        new ItemActorAuthority(ItemTransactionActor.ForAccount(AccountId)).EnsureLiveContainerAccess("permanent_inventory");

        AssertRejected(
            () => new ItemActorAuthority(ItemTransactionActor.ForAccount(AccountId)).EnsureLiveContainerAccess("bank"),
            ItemTransactionErrorCodes.BankAccessRequired,
            "Bank access requires the character at a city bank in the world.");
        AssertRejected(
            () => new ItemActorAuthority(Simulation()).EnsureLiveContainerAccess("bank"),
            ItemTransactionErrorCodes.BankAccessRequired,
            "The character is outside validated bank access.");
        AssertRejected(
            () => new ItemActorAuthority(Simulation()).EnsureLiveContainerAccess("recovery_storage"),
            ItemTransactionErrorCodes.RecoveryAccessRequired,
            "The character is outside validated Recovery Storage access.");
    }

    private static ItemTransactionActor Simulation(ItemTransactionLiveAccess? access = null)
    {
        return ItemTransactionActor.ForSimulationWorker(
            AccountId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "worker-1",
            "runtime-1",
            "shard-1",
            "token-hash",
            access ?? ItemTransactionLiveAccess.None);
    }

    private static void AssertInvalid(ItemTransactionActor actor, string message)
    {
        AssertRejected(
            () => ItemActorAuthority.EnsureValid(actor),
            ItemTransactionErrorCodes.AuthorityRequired,
            message);
    }

    private static void AssertRejected(Action action, string code, string message)
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
    }
}
