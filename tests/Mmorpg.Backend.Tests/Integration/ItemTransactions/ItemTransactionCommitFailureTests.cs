using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Npgsql;
using static Mmorpg.Backend.Tests.Integration.ItemTransactions.ItemTransactionKernelTests;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// A transaction that already ended, through a failed commit or a rollback, is never
// rolled back again, so the original error stays visible.
public sealed class ItemTransactionCommitFailureTests
{
    [PostgresIntegrationFact]
    public async Task CommitTimeSlotConflictIsReturnedWithoutBeingStored()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("commit_conflict");
        var state = await world.LoadStateAsync(player.Character.Id);
        await world.GrantAsync(player.Character.Id, "material.iron_ore", 1, state.PermanentInventoryContainerId, 0);
        var secondId = await world.GrantAsync(
            player.Character.Id,
            "material.iron_ore",
            1,
            state.PermanentInventoryContainerId,
            1);
        var kernel = CreateKernel(context);
        var request = Request();

        var result = await kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            null,
            new TestHandler(async (transactionContext, _) =>
            {
                // The deferred unique slot constraint fails only at COMMIT.
                await transactionContext.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    set constraints ux_item_instances_container_slot deferred;

                    update item_instances
                    set container_slot_index = 0
                    where id = @ItemId;
                    """,
                    new { ItemId = secondId },
                    transactionContext.Transaction));
                transactionContext.Journal.AddMetadataAudit(
                    "commit_conflict_test",
                    new { Slot = 1 },
                    new { Slot = 0 });
            }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemSlotOccupied, result.Error!.Code);
        Assert.Null(await LoadStatusAsync(context, request.OperationId));
        Assert.Equal(1, (await world.LoadItemAsync(secondId)).ContainerSlotIndex);
    }

    [PostgresIntegrationFact]
    public async Task ReplayLookupFailureAfterLiveAuthorityRejectionKeepsTheOriginalError()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);
        await using var unreachable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var kernel = CreateKernel(context, ledgerDataSource: unreachable);
        var request = new ItemTransactionRequest<GrantItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSimulationWorker(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "worker-1",
                "runtime-1",
                "shard-1",
                "token-hash",
                ItemTransactionLiveAccess.None),
            new GrantItemCommand(Guid.NewGuid(), 1, "material.iron_ore", 1, Guid.NewGuid(), 0));

        // The command character does not match the simulation character, so live
        // authority rejects; the committed replay lookup then cannot connect.
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            request.Command.CharacterId,
            new TestHandler((_, _) => Task.CompletedTask),
            CancellationToken.None));
    }
}
