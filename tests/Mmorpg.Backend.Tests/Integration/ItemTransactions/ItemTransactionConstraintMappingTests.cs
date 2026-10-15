using Dapper;
using Mmorpg.GameService.Items;
using static Mmorpg.Backend.Tests.Integration.ItemTransactions.ItemTransactionKernelTests;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Deferred foreign keys that fail at commit and duplicate item ids are stable
// rejections instead of unexpected failures.
public sealed class ItemTransactionConstraintMappingTests
{
    [PostgresIntegrationFact]
    public async Task DeferredForeignKeyFailureAtCommitIsAStateConflict()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("deferred_foreign_key");
        var state = await world.LoadStateAsync(player.Character.Id);
        var itemId = await world.GrantAsync(player.Character.Id, "material.iron_ore", 1, state.PermanentInventoryContainerId, 0);
        var kernel = CreateKernel(context);
        var request = Request();

        var result = await kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            null,
            new TestHandler(async (transactionContext, _) =>
            {
                // No slot 999 exists; the deferred slot foreign key fails only at COMMIT.
                await transactionContext.Connection.ExecuteAsync(new CommandDefinition(
                    "update item_instances set container_slot_index = 999 where id = @ItemId;",
                    new { ItemId = itemId },
                    transactionContext.Transaction));
                transactionContext.Journal.AddMetadataAudit(
                    "deferred_foreign_key_test",
                    new { Slot = 0 },
                    new { Slot = 999 });
            }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, result.Error!.Code);
        Assert.Equal("The item state changed before the operation committed.", result.Error.Message);
        Assert.Null(await LoadStatusAsync(context, request.OperationId));
        Assert.Equal(0, (await world.LoadItemAsync(itemId)).ContainerSlotIndex);
    }

    [PostgresIntegrationFact]
    public async Task DuplicateItemInstanceIdIsAnOperationConflict()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("duplicate_item_id");
        var state = await world.LoadStateAsync(player.Character.Id);
        var itemId = await world.GrantAsync(player.Character.Id, "material.iron_ore", 1, state.PermanentInventoryContainerId, 0);
        var kernel = CreateKernel(context);
        var request = Request();

        var result = await kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            null,
            new TestHandler(async (transactionContext, _) =>
            {
                await transactionContext.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_instances (id, definition_id, quantity, container_id, container_slot_index)
                    values (@ItemId, 'material.iron_ore', 1, @ContainerId, 1);
                    """,
                    new { ItemId = itemId, ContainerId = state.PermanentInventoryContainerId },
                    transactionContext.Transaction));
            }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationConflict, result.Error!.Code);
        Assert.Equal("The item instance already exists.", result.Error.Message);
        Assert.Equal("rejected", await LoadStatusAsync(context, request.OperationId));
    }
}
