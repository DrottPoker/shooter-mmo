using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// Items are deleted all the time: a merged stack, a consumed or destroyed item. A delete finds
// every row that refers to the item through an index, and the journal and the destruction
// records keep the ids of rows that are gone.
public sealed class ItemForeignKeyIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task DeletingAnItemScansNoTable()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("item_delete_scan");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var ringId = await world.GrantAsync(
            characterId,
            "fixture.ring",
            1,
            state.PermanentInventoryContainerId,
            0);
        await world.ApplyPolicyAsync(characterId, ringId, ItemPolicyIds.Insured);

        // A connection of its own, so no key check reuses a plan from another test, and with
        // sequential scans priced out, so a scan that remains has no index it could use.
        var connectionString = new NpgsqlConnectionStringBuilder(context.ConnectionString)
        {
            Pooling = false
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("set local enable_seqscan = off;", transaction: transaction);
        var before = await SequentialScansAsync(connection, transaction);

        Assert.Equal(
            1,
            await connection.ExecuteAsync(
                "delete from item_instances where id = @ItemId;",
                new { ItemId = ringId },
                transaction));
        // Runs the deferred key checks, such as the cascade to a Bag's contents, now.
        await connection.ExecuteAsync("set constraints all immediate;", transaction: transaction);
        var after = await SequentialScansAsync(connection, transaction);
        await transaction.RollbackAsync();

        var scanned = after
            .Where(table => table.Value > before.GetValueOrDefault(table.Key))
            .Select(table => table.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            scanned.Length == 0,
            "Deleting an item scanned " + string.Join(", ", scanned) + ".");
    }

    [PostgresIntegrationFact]
    public async Task TheJournalAndTheDestructionKeepTheIdsOfRowsThatAreGone()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("item_delete_history");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var ringId = await world.GrantAsync(
            characterId,
            "fixture.ring",
            1,
            state.PermanentInventoryContainerId,
            0);
        state = await world.LoadStateAsync(characterId);
        var ring = await world.LoadItemAsync(ringId);
        var destroyId = Guid.NewGuid();
        var destroy = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                destroyId,
                ItemTransactionActor.ForSystem(),
                new DestroyItemCommand(characterId, state.Revision, ringId, ring.Revision, "history")),
            CancellationToken.None);
        Assert.True(destroy.Succeeded, destroy.Error?.Message);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ItemId;",
                new { ItemId = ringId }));
        // The grant's row and the destruction's removal row both still name the ring.
        Assert.Equal(
            new[] { (false, false), (true, true) },
            (await connection.QueryAsync<(bool IsDestruction, bool IsRemoval)>(
                """
                select operation_id = @OperationId, after_state is null
                from item_operation_changes
                where item_instance_id = @ItemId
                order by after_state is null;
                """,
                new { ItemId = ringId, OperationId = destroyId })).ToArray());

        // Retention may remove the operation before its destruction record.
        Assert.Equal(
            1,
            await connection.ExecuteAsync(
                "delete from item_operations where operation_id = @OperationId;",
                new { OperationId = destroyId }));
        Assert.Equal(
            destroyId,
            await connection.ExecuteScalarAsync<Guid?>(
                "select source_operation_id from item_destructions where item_instance_id = @ItemId;",
                new { ItemId = ringId }));
    }

    private static async Task<Dictionary<string, long>> SequentialScansAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        // The counts of this transaction, before the session reports them.
        var rows = await connection.QueryAsync<(string Table, long Scans)>(
            "select relname::text, seq_scan from pg_stat_xact_user_tables;",
            transaction: transaction);
        return rows.ToDictionary(row => row.Table, row => row.Scans, StringComparer.Ordinal);
    }
}
