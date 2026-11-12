using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

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

    // A collect objective of the Admin menu's quest tools can lack more than one stack.
    [Fact]
    public void AnAcquisitionOfSeveralStacksTakesFullStacksThenTheRest()
    {
        Assert.Equal([10, 10, 5], CarriedAcquisition.StackSizedParts(25, 10));
        Assert.Equal([3], CarriedAcquisition.StackSizedParts(3, 100));
        Assert.Equal([1, 1], CarriedAcquisition.StackSizedParts(2, 1));
        Assert.Equal(
            ItemTransactionErrorCodes.ItemStackLimitExceeded,
            Assert.Throws<ItemTransactionRejectedException>(() => CarriedAcquisition.StackSizedParts(0, 10)).Code);
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

    [Theory]
    [InlineData("c0000000-0000-4000-8000-000000000001", -2075836076, 1073741952)]
    [InlineData("12345678-9abc-4def-8123-456789abcdef", 1450252076, 715831613)]
    public void DeathEventLockKeysKeepTheirDerivation(string id, int firstKey, int secondKey)
    {
        Assert.Equal((firstKey, secondKey), DurableCorpseRecords.DeathEventLockKeys(Guid.Parse(id)));
    }

    [Fact]
    public void CorpseTransformRulesBoundPositionsAndNormalizeRotations()
    {
        Assert.Equal("corpse.remains", CorpseTransformRules.PlayerCorpsePresentationKey);
        // A Mob corpse is the generic remains or a Mob's own, named after its presentation archetype.
        Assert.True(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.mob"));
        Assert.True(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.mob.feral_wolf"));
        Assert.False(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.remains"));
        Assert.False(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.mob."));
        Assert.False(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.mobile"));
        Assert.False(CorpseTransformRules.IsMobCorpsePresentationKey("corpse.mob.Feral Wolf"));
        Assert.False(CorpseTransformRules.IsMobCorpsePresentationKey(null));
        Assert.Equal("corpse.mob.feral_wolf", WorldActorCorpseRules.MobCorpsePresentationKeyFor("mob.feral_wolf"));
        Assert.Equal("corpse.mob", WorldActorCorpseRules.MobCorpsePresentationKeyFor("npc.city_guard"));
        Assert.True(CorpseTransformRules.IsFinitePosition(1_000_000d));
        Assert.False(CorpseTransformRules.IsFinitePosition(1_000_000.5d));
        Assert.False(CorpseTransformRules.IsFinitePosition(double.NaN));
        Assert.True(CorpseTransformRules.IsNormalizedRotation(0d, 0.6d, 0d, 0.8d));
        Assert.False(CorpseTransformRules.IsNormalizedRotation(0d, 0d, 0d, 0.99d));
        Assert.False(CorpseTransformRules.IsNormalizedRotation(0d, 0d, 0d, double.PositiveInfinity));
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
