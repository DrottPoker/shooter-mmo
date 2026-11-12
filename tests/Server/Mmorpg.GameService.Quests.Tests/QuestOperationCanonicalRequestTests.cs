using System.Security.Cryptography;
using System.Text;
using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// Pins the persisted idempotency format of the quest commands as ItemOperationCanonicalRequestTests
// does for the item commands: the canonical request payload whose SHA-256 is stored as
// item_operations.request_hash. The expected JSON is written by hand.
public sealed class QuestOperationCanonicalRequestTests
{
    private const string SystemActor =
        """{"authority":2,"accountId":null,"requiresOfflineCharacter":false,"simulationSessionId":null,"characterId":null,"workerId":null,"workerRuntimeId":null,"shardId":null}""";

    private static readonly Guid A = Guid.Parse("a0000000-0000-4000-8000-000000000001");

    [PostgresIntegrationFact]
    public async Task AStoredRejectionKeepsTheCanonicalRequestHashOfAbandonQuestItems()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();

        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(A, 1, "quest-9")),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(QuestOperationKinds.AbandonQuestItems, result.OperationKind);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<OperationRow>(
            """
            select
                operation_kind as "OperationKind",
                request_hash as "RequestHash",
                actor_account_id as "ActorAccountId",
                actor_character_id as "ActorCharacterId"
            from item_operations
            where operation_id = @OperationId;
            """,
            new { result.OperationId });
        Assert.Equal(QuestOperationKinds.AbandonQuestItems, row.OperationKind);
        var command = $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"questGrantId":"quest-9"}""";
        Assert.Equal(Hash($$"""{"actor":{{SystemActor}},"command":{{command}}}"""), row.RequestHash);
        Assert.Null(row.ActorAccountId);
        Assert.Null(row.ActorCharacterId);
    }

    private static string Hash(string json)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private sealed class OperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public Guid? ActorAccountId { get; set; }

        public Guid? ActorCharacterId { get; set; }
    }
}
