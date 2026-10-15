using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.Testing;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Pins the replay semantics of the operation ledger that callers rely on.
public sealed class ItemTransactionKernelEdgeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresIntegrationFact]
    public async Task LedgerReplaysStoredRejectionsReportsPendingClaimsAndReleasesRetriedRejections()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var service = context.ItemTransactionService;
        var player = await world.RegisterAsync("kernel_edges");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var itemId = await world.GrantAsync(
            characterId,
            "material.iron_ore",
            1,
            state.PermanentInventoryContainerId,
            0);
        state = await world.LoadStateAsync(characterId);
        var item = await world.LoadItemAsync(itemId);

        // An account rejection is stored and replays identically.
        var staleRequest = new ItemTransactionRequest<RelocateItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForAccount(player.Registration.AccountId),
            new RelocateItemCommand(
                characterId,
                state.Revision,
                itemId,
                item.Revision + 1,
                state.PermanentInventoryContainerId,
                1));
        var rejected = await service.ExecuteAsync(staleRequest, CancellationToken.None);
        var replayed = await service.ExecuteAsync(staleRequest, CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, rejected.Error!.Code);
        Assert.Equal(
            JsonSerializer.Serialize(rejected, JsonOptions),
            JsonSerializer.Serialize(replayed, JsonOptions));
        Assert.Equal("rejected", await LoadStatusAsync(context, staleRequest.OperationId));

        // A pending claim with the same request is reported as pending, not executed.
        await world.ExecuteSqlAsync(
            """
            update item_operations
            set status = 'pending',
                result_payload = null,
                completed_at = null
            where operation_id = @OperationId;
            """,
            new { staleRequest.OperationId });
        var pending = await service.ExecuteAsync(staleRequest, CancellationToken.None);
        Assert.False(pending.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationConflict, pending.Error!.Code);
        Assert.Equal("The operation id is already pending.", pending.Error.Message);
        Assert.Equal("pending", await LoadStatusAsync(context, staleRequest.OperationId));

        // A different request with a used id is a conflict.
        var conflict = await service.ExecuteAsync(
            staleRequest with { Command = staleRequest.Command with { DestinationSlotIndex = 2 } },
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationConflict, conflict.Error!.Code);
        Assert.Equal("The operation id was already used with a different request.", conflict.Error.Message);

        // A rejected retried system operation leaves no claim behind.
        var expiryId = Guid.NewGuid();
        var expiry = await service.ExecuteAsync(
            new ItemTransactionRequest<ExpireCorpseCommand>(
                expiryId,
                ItemTransactionActor.ForSystem(),
                new ExpireCorpseCommand(Guid.NewGuid())),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.CorpseNotFound, expiry.Error!.Code);
        Assert.Null(await LoadStatusAsync(context, expiryId));
    }

    private static async Task<string?> LoadStatusAsync(
        PostgresIntegrationTestContext context,
        Guid operationId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "select status from item_operations where operation_id = @OperationId;",
            new { OperationId = operationId });
    }
}
