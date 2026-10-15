using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.Backend.Tests.Unit;

// White-box tests of the pure domain helpers shared by item command handlers.
public sealed class ItemTransactionDomainRulesTests
{
    [Fact]
    public void PolicyFingerprintIgnoresOrderAndRevisionAndHashesTheSortedRows()
    {
        var protectedPolicy = Policy("protected_on_death", "quest_grant", "quest-1", revision: 4);
        var insuredPolicy = Policy("insured", "insurance_service", "npc", revision: 0);

        var forward = ItemPolicyStates.CalculatePolicyFingerprint([protectedPolicy, insuredPolicy]);
        var reversed = ItemPolicyStates.CalculatePolicyFingerprint([insuredPolicy, protectedPolicy]);

        Assert.Equal(forward, reversed);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    "insured|active|insurance_service|npc\nprotected_on_death|active|quest_grant|quest-1"))),
            forward);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ItemPolicyStates.CalculatePolicyFingerprint([]));
        Assert.Equal("item-stack-state-v1", ItemPolicyStates.EmptyStackStateFingerprint);
    }

    [Fact]
    public void PolicyStatesCarryKindStatusAndLineage()
    {
        var state = Assert.Single(ItemPolicyStates.ToPolicyStates(
            [Policy("insured", "insurance_service", "npc", revision: 2)]));

        Assert.Equal("insured", state.PolicyKind);
        Assert.Equal("active", state.Status);
        Assert.Equal("insurance_service", state.SourceKind);
        Assert.Equal("npc", state.SourceId);
    }

    [Fact]
    public void RecoveryValidatorsRejectWithStableCodes()
    {
        var item = Guid.NewGuid();
        Assert.Equal(
            new Dictionary<Guid, long> { [item] = 3 },
            RecoveryDeliveries.ValidateDistinctItemExpectations([new ItemRevisionExpectation(item, 3)]));
        AssertRejected(
            () => RecoveryDeliveries.ValidateDistinctItemExpectations([]),
            ItemTransactionErrorCodes.ItemNotFound,
            "At least one item revision expectation is required.");
        AssertRejected(
            () => RecoveryDeliveries.ValidateDistinctItemExpectations(
                [new ItemRevisionExpectation(item, 1), new ItemRevisionExpectation(item, 1)]),
            ItemTransactionErrorCodes.ItemStateConflict,
            "Item revision expectations must contain unique valid item ids and revisions.");
        AssertRejected(
            () => RecoveryDeliveries.ValidateRecoverySource("source", " "),
            ItemTransactionErrorCodes.ItemOperationConflict,
            "A Recovery Storage source kind and source event id are required.");
        AssertRejected(
            () => RecoveryDeliveries.ValidateRecoveryTimeline(DateTime.Now, null),
            ItemTransactionErrorCodes.ItemOperationConflict,
            "Recovery Storage availability time must use UTC.");
        AssertRejected(
            () => RecoveryDeliveries.ValidateRecoveryTimeline(
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(-1)),
            ItemTransactionErrorCodes.ItemOperationConflict,
            "Recovery Storage expiry cannot precede availability.");
    }

    private static void AssertRejected(Action action, string code, string message)
    {
        var exception = Assert.Throws<ItemTransactionRejectedException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
    }

    private static LockedItemPolicy Policy(string kind, string sourceKind, string sourceId, long revision)
    {
        return new LockedItemPolicy
        {
            PolicyId = Guid.NewGuid(),
            PolicyKind = kind,
            Status = "active",
            SourceKind = sourceKind,
            SourceId = sourceId,
            Revision = revision
        };
    }
}
