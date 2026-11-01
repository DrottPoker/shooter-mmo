using System.Security.Cryptography;
using System.Text;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// White-box tests of the admission checks and the canonical request format that the
// transaction kernel computes before it opens a connection.
public sealed class ItemOperationAdmissionTests
{
    private static readonly Guid First = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid Second = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid Third = Guid.Parse("b0000000-0000-4000-8000-000000000003");

    [Fact]
    public void CanonicalRequestIsTheStoredJsonWithSortedClaimItems()
    {
        var canonical = ItemOperationRequestCanonicalizer.Canonicalize(
            ItemTransactionActor.ForSystem(),
            new ClaimRecoveryDeliveryCommand(
                First,
                4,
                Second,
                1,
                Third,
                [new ItemRevisionExpectation(Third, 2), new ItemRevisionExpectation(Second, 7)]));

        const string expected =
            """{"actor":{"authority":2,"accountId":null,"requiresOfflineCharacter":false,"simulationSessionId":null,"characterId":null,"workerId":null,"workerRuntimeId":null,"shardId":null},"command":{"characterId":"b0000000-0000-4000-8000-000000000001","expectedCharacterRevision":4,"recoveryDeliveryId":"b0000000-0000-4000-8000-000000000002","expectedRecoveryDeliveryRevision":1,"destinationContainerId":"b0000000-0000-4000-8000-000000000003","items":[{"itemInstanceId":"b0000000-0000-4000-8000-000000000002","revision":7},{"itemInstanceId":"b0000000-0000-4000-8000-000000000003","revision":2}]}}""";
        Assert.Equal(expected, canonical.Payload);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))),
            canonical.Hash);
    }

    [Fact]
    public void CanonicalSimulationActorOmitsCredentialsAndLiveAccess()
    {
        var canonical = ItemOperationRequestCanonicalizer.Canonicalize(
            ItemTransactionActor.ForSimulationWorker(
                First,
                Second,
                Third,
                "worker-1",
                "runtime-1",
                "shard-1",
                "secret-token-hash",
                new ItemTransactionLiveAccess(true, true, true, true)),
            new DestroyItemCommand(Second, 3, Third, 5, "player_destroyed"));

        Assert.Equal(
            """{"actor":{"authority":3,"accountId":"b0000000-0000-4000-8000-000000000001","requiresOfflineCharacter":false,"simulationSessionId":"b0000000-0000-4000-8000-000000000003","characterId":"b0000000-0000-4000-8000-000000000002","workerId":"worker-1","workerRuntimeId":"runtime-1","shardId":"shard-1"},"command":{"characterId":"b0000000-0000-4000-8000-000000000002","expectedCharacterRevision":3,"itemInstanceId":"b0000000-0000-4000-8000-000000000003","expectedItemRevision":5,"reason":"player_destroyed"}}""",
            canonical.Payload);
    }

    [Theory]
    [InlineData((int)ItemTransactionAuthority.Account, ItemOperationKinds.Relocate, true)]
    [InlineData((int)ItemTransactionAuthority.Account, ItemOperationKinds.ProcessPlayerDeath, true)]
    [InlineData((int)ItemTransactionAuthority.SimulationWorker, ItemOperationKinds.Relocate, true)]
    [InlineData((int)ItemTransactionAuthority.SimulationWorker, ItemOperationKinds.GrantMobLoot, false)]
    [InlineData((int)ItemTransactionAuthority.SimulationWorker, ItemOperationKinds.ProcessPlayerDeath, false)]
    [InlineData((int)ItemTransactionAuthority.System, ItemOperationKinds.Relocate, false)]
    [InlineData((int)ItemTransactionAuthority.System, ItemOperationKinds.ExpireCorpse, false)]
    public void RandomClientOperationIdsAreRequiredForPlayerChosenIdsOnly(
        int authority,
        string operationKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            ItemOperationAdmission.RequiresRandomClientOperationId(
                new ItemTransactionActor((ItemTransactionAuthority)authority, null),
                operationKind));
    }

    [Fact]
    public void PayloadLimitCountsUtf8BytesAndAllowsTheExactLimit()
    {
        var options = ItemOperationsOptions.CreateDefaults() with { MaximumCommandPayloadBytes = 1024 };

        Assert.False(ItemOperationAdmission.ExceedsPayloadLimit(new string('a', 1024), options));
        Assert.True(ItemOperationAdmission.ExceedsPayloadLimit(new string('a', 1025), options));
        Assert.True(ItemOperationAdmission.ExceedsPayloadLimit(new string('å', 513), options));
    }
}
