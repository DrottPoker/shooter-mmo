using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// The retention tasks of item history: what each trims, in which order, and what it keeps. Rows
// are made due by moving their time back past a one-day window.
public sealed class ItemRetentionIntegrationTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task ItemHistoryGoesOnceDueWithItsJournalAndWhatIsNotDueStays()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_history");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var ringId = await world.GrantAsync(characterId, "fixture.ring", 1, inventoryId, 0);
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 2, inventoryId, 1);
        var destroyId = await DestroyAsync(world, characterId, ringId);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var ringGrantId = await OperationOfAsync(connection, ringId, "grant");
        var oreGrantId = await OperationOfAsync(connection, oreId, "grant");
        var pendingId = await InsertPendingOperationAsync(connection);
        await connection.ExecuteAsync(
            """
            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '2 days'
            where operation_id = any(@OperationIds);

            update item_destructions
            set destroyed_at = now() - interval '2 days'
            where item_instance_id = @RingId;
            """,
            new { OperationIds = new[] { ringGrantId, destroyId }, RingId = ringId });

        var pass = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(2, pass.Task("item_operations").Deleted);
        Assert.Equal(1, pass.Task("item_destructions").Deleted);
        Assert.Equal(
            new[] { oreGrantId, pendingId }.Order(),
            (await connection.QueryAsync<Guid>(
                "select operation_id from item_operations where operation_id = any(@OperationIds);",
                new { OperationIds = new[] { ringGrantId, oreGrantId, destroyId, pendingId } })).Order());
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operation_changes where operation_id = any(@OperationIds);",
                new { OperationIds = new[] { ringGrantId, destroyId } }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_destructions where item_instance_id = @RingId;",
                new { RingId = ringId }));

        // A pending operation is never due, however old it is.
        await connection.ExecuteAsync(
            "update item_operations set created_at = now() - interval '30 days' where operation_id = @PendingId;",
            new { PendingId = pendingId });
        Assert.Equal(0, (await RetentionPasses.RunAsync(context, Day, Day)).Task("item_operations").Deleted);
    }

    [PostgresIntegrationFact]
    public async Task TheOperationOfADeathStaysUntilItsDeathEventLeftWithItsClosedCorpse()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_death");
        var corpseId = await world.ProcessDeathAsync(player.Character.Id);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var deathOperationId = await connection.QuerySingleAsync<Guid>(
            "select operation_id from death_events where corpse_id = @CorpseId;",
            new { CorpseId = corpseId });
        await connection.ExecuteAsync(
            """
            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '2 days'
            where operation_id = @OperationId;
            """,
            new { OperationId = deathOperationId });

        // The death event still holds the operation of the open corpse.
        var open = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, open.Task("item_operations").Deleted);
        Assert.Equal(0, open.Task("closed_corpses").Deleted);

        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '5 minutes'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        Assert.Equal(1, await context.CorpseService.ExpireDueCorpsesAsync(8, CancellationToken.None));
        var containerIds = (await connection.QueryAsync<Guid>(
            "select container_id from corpse_sections where corpse_id = @CorpseId;",
            new { CorpseId = corpseId })).ToArray();

        // Closed today, the corpse is not due yet, so its death event still holds the operation.
        var closed = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, closed.Task("closed_corpses").Deleted);
        Assert.Equal(0, closed.Task("item_operations").Deleted);

        await connection.ExecuteAsync(
            "update corpses set closed_at = now() - interval '2 days' where id = @CorpseId;",
            new { CorpseId = corpseId });
        var due = await RetentionPasses.RunAsync(context, Day, Day);

        // The corpse goes first in a pass, so its death's operation goes in the same pass.
        Assert.Equal(1, due.Task("closed_corpses").Deleted);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select (select count(*) from corpses where id = @CorpseId)
                     + (select count(*) from death_events where corpse_id = @CorpseId)
                     + (select count(*) from item_containers where id = any(@ContainerIds))
                     + (select count(*) from item_operations where operation_id = @OperationId);
                """,
                new { CorpseId = corpseId, ContainerIds = containerIds, OperationId = deathOperationId }));
    }

    [PostgresIntegrationFact]
    public async Task AClosedCorpseThatStillHoldsAnItemStays()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_full_corpse");
        var inventoryId = (await world.LoadStateAsync(player.Character.Id)).PermanentInventoryContainerId;
        await world.GrantAsync(player.Character.Id, "fixture.ore", 3, inventoryId, 0);
        var corpseId = await world.ProcessDeathAsync(player.Character.Id);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '5 minutes',
                closed_at = now() - interval '2 days',
                close_reason = 'administrative'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });

        var pass = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(0, pass.Task("closed_corpses").Deleted);
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from death_events where corpse_id = @CorpseId;",
                new { CorpseId = corpseId }));
    }

    [PostgresIntegrationFact]
    public async Task LiveLootGrantsGoOnceDueAndAnUnclaimedDeliveryStays()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_grants");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 1, inventoryId, 0);
        var deliveryId = await world.AddRecoveryDeliveryAsync(characterId, oreId);
        var oldGrant = Guid.NewGuid();
        var newGrant = Guid.NewGuid();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into live_mob_loot_grants (grant_id, operation_id, character_id, source_corpse_id, granted_at)
            values
                (@OldGrant, @OldGrant, @CharacterId, gen_random_uuid(), now() - interval '2 days'),
                (@NewGrant, @NewGrant, @CharacterId, gen_random_uuid(), now());

            update recovery_deliveries
            set created_at = now() - interval '30 days'
            where id = @DeliveryId;
            """,
            new { OldGrant = oldGrant, NewGrant = newGrant, CharacterId = characterId, DeliveryId = deliveryId });

        var pass = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(1, pass.Task("live_mob_loot_grants").Deleted);
        Assert.Equal(0, pass.Task("claimed_recovery_deliveries").Deleted);
        Assert.Equal(
            [newGrant],
            (await connection.QueryAsync<Guid>(
                "select grant_id from live_mob_loot_grants where grant_id = any(@GrantIds);",
                new { GrantIds = new[] { oldGrant, newGrant } })).ToArray());
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from recovery_deliveries where id = @DeliveryId;",
                new { DeliveryId = deliveryId }));
    }

    // A batch locks only rows that nobody holds; a row that play holds stays for a later pass, and
    // the batch neither waits for it nor fails on the lock timeout.
    [PostgresIntegrationFact]
    public async Task ABatchSkipsARowThatPlayHoldsInsteadOfWaitingForIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_locked");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var first = await world.GrantAsync(characterId, "fixture.ore", 1, inventoryId, 0);
        var second = await world.GrantAsync(characterId, "fixture.ring", 1, inventoryId, 1);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var held = await OperationOfAsync(connection, first, "grant");
        var free = await OperationOfAsync(connection, second, "grant");
        await connection.ExecuteAsync(
            "update item_operations set completed_at = now() - interval '2 days' where operation_id = any(@OperationIds);",
            new { OperationIds = new[] { held, free } });
        await using var play = await context.DataSource.OpenConnectionAsync();
        await using var playTransaction = await play.BeginTransactionAsync();
        await play.ExecuteAsync(
            "select 1 from item_operations where operation_id = @OperationId for update;",
            new { OperationId = held },
            playTransaction);

        var pass = await RetentionPasses.RunAsync(context, Day, Day);
        await playTransaction.RollbackAsync();

        Assert.False(pass.Task("item_operations").Failed);
        Assert.Equal(1, pass.Task("item_operations").Deleted);
        Assert.Equal(
            [held],
            (await connection.QueryAsync<Guid>(
                "select operation_id from item_operations where operation_id = any(@OperationIds);",
                new { OperationIds = new[] { held, free } })).ToArray());
    }

    [PostgresIntegrationFact]
    public async Task ABacklogDrainsInBatchesWithinOnePassAndTheLagSaysHowLongItWaited()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("retention_backlog");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var itemIds = new List<Guid>();
        for (var slot = 0; slot < 5; slot++)
        {
            itemIds.Add(await world.GrantAsync(characterId, "fixture.ore", 1, inventoryId, slot));
        }

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var operationIds = new List<Guid>();
        foreach (var itemId in itemIds)
        {
            operationIds.Add(await OperationOfAsync(connection, itemId, "grant"));
        }

        await connection.ExecuteAsync(
            """
            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '1 day 10 minutes'
            where operation_id = any(@OperationIds);
            """,
            new { OperationIds = operationIds.ToArray() });

        var pass = await RetentionPasses.RunAsync(context, Day, Day, batchSize: 2);

        var operations = pass.Task("item_operations");
        Assert.Equal(5, operations.Deleted);
        Assert.Equal(3, operations.Batches);
        Assert.False(operations.MoreDue);
        Assert.InRange(operations.Lag!.Value, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(11));
    }

    private static async Task<Guid> DestroyAsync(ItemScenarioWorld world, Guid characterId, Guid itemId)
    {
        var state = await world.LoadStateAsync(characterId);
        var item = await world.LoadItemAsync(itemId);
        var operationId = Guid.NewGuid();
        var result = await world.Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new DestroyItemCommand(characterId, state.Revision, itemId, item.Revision, "retention")),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return operationId;
    }

    private static Task<Guid> OperationOfAsync(NpgsqlConnection connection, Guid itemId, string kind)
    {
        return connection.QuerySingleAsync<Guid>(
            """
            select distinct change.operation_id
            from item_operation_changes change
            join item_operations operation on operation.operation_id = change.operation_id
            where change.item_instance_id = @ItemId
              and operation.operation_kind = @Kind;
            """,
            new { ItemId = itemId, Kind = kind });
    }

    private static async Task<Guid> InsertPendingOperationAsync(NpgsqlConnection connection)
    {
        var operationId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            insert into item_operations (operation_id, operation_kind, request_hash, request_payload, created_at)
            values (@OperationId, 'grant', @RequestHash, '{}'::jsonb, now() - interval '3 days');
            """,
            new { OperationId = operationId, RequestHash = new string('b', 64) });
        return operationId;
    }
}
