using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Identity;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// A retried operation id is answered from item_operations while its row exists. Once retention
// trimmed the row, the same id runs again, so every deterministic id of a system operation must be
// safe to run again or be refused by durable state. Each test trims the operation and replays it.
public sealed class RetentionIdempotencyIntegrationTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task ARecoveryExpiryRunAgainFindsNoDeliveryAndStoresNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("replay_recovery");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        var oreId = await world.GrantAsync(characterId, "fixture.ore", 1, inventoryId, 0);
        var deliveryId = await world.AddRecoveryDeliveryAsync(characterId, oreId, DateTime.UtcNow.AddMinutes(5));
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update recovery_deliveries
            set created_at = now() - interval '2 seconds',
                expires_at = now() - interval '1 second'
            where id = @DeliveryId;
            """,
            new { DeliveryId = deliveryId });
        var operationId = ScopedGuid.Create("recovery-expiry", deliveryId);
        var expired = await ExpireRecoveryAsync(context, operationId, deliveryId);
        Assert.True(expired.Succeeded, expired.Error?.Message);
        await TrimOperationAsync(context, connection, operationId);

        var replay = await ExpireRecoveryAsync(context, operationId, deliveryId);

        Assert.False(replay.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.RecoveryDeliveryNotFound, replay.Error!.Code);
        Assert.Equal(0, await OperationCountAsync(connection, operationId));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_destructions where item_instance_id = @OreId;",
                new { OreId = oreId }));
    }

    [PostgresIntegrationFact]
    public async Task ACorpseExpiryRunAgainOnlyRecordsItsReplayAndFindsNoTrimmedCorpse()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("replay_corpse_expiry");
        var inventoryId = (await world.LoadStateAsync(player.Character.Id)).PermanentInventoryContainerId;
        await world.GrantAsync(player.Character.Id, "fixture.ore", 3, inventoryId, 0);
        var corpseId = await world.ProcessDeathAsync(player.Character.Id);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '5 minutes'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        var expired = await context.CorpseService.ExpireCorpseAsync(corpseId, CancellationToken.None);
        Assert.True(expired.Succeeded, expired.Error?.Message);
        var expiryOperationId = expired.Value!.OperationId;
        var destructions = await DestructionCountAsync(connection);
        await TrimOperationAsync(context, connection, expiryOperationId);

        // The closed corpse is still there, so its stored expiry id runs again as a replay that
        // destroys nothing.
        var replay = await context.CorpseService.ExpireCorpseAsync(corpseId, CancellationToken.None);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.Equal(expiryOperationId, replay.Value!.OperationId);
        Assert.Equal(destructions, await DestructionCountAsync(connection));

        await connection.ExecuteAsync(
            "update corpses set closed_at = now() - interval '2 days' where id = @CorpseId;",
            new { CorpseId = corpseId });
        Assert.Equal(1, (await RetentionPasses.RunAsync(context, Day, Day)).Task("closed_corpses").Deleted);
        var afterTrim = await context.CorpseService.ExpireCorpseAsync(corpseId, CancellationToken.None);
        Assert.False(afterTrim.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CorpseNotFound, afterTrim.Error!.Code);
        Assert.Equal(destructions, await DestructionCountAsync(connection));
    }

    // The death event leaves with its corpse, and the death's operation after it. A replay with the
    // same ids then finds the character's revision advanced and partitions nothing.
    [PostgresIntegrationFact]
    public async Task APlayerDeathRunAgainAfterItsOperationAndEventWereTrimmedPartitionsNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("replay_death");
        var characterId = player.Character.Id;
        var inventoryId = (await world.LoadStateAsync(characterId)).PermanentInventoryContainerId;
        await world.GrantAsync(characterId, "fixture.ore", 3, inventoryId, 0);
        var state = await world.LoadStateAsync(characterId);
        var operationId = ScopedGuid.Create("player-death-operation", Guid.NewGuid());
        var command = ItemScenarioWorld.CreateDeathCommand(characterId, state.Revision);
        var death = await DieAsync(context, operationId, command);
        Assert.True(death.Succeeded, death.Error?.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var corpseId = await connection.QuerySingleAsync<Guid>(
            "select corpse_id from death_events where operation_id = @OperationId;",
            new { OperationId = operationId });
        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '5 minutes'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        Assert.Equal(1, await context.CorpseService.ExpireDueCorpsesAsync(8, CancellationToken.None));
        await connection.ExecuteAsync(
            "update corpses set closed_at = now() - interval '2 days' where id = @CorpseId;",
            new { CorpseId = corpseId });
        await TrimOperationAsync(context, connection, operationId);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from death_events where id = @DeathEventId;",
                new { command.DeathEventId }));

        var replay = await DieAsync(context, operationId, command);

        Assert.False(replay.Succeeded);
        Assert.Equal(0, await OperationCountAsync(connection, operationId));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from corpses where source_character_id = @CharacterId;",
                new { CharacterId = characterId }));
    }

    // While the corpse exists, its row refuses a second creation with the same id.
    [PostgresIntegrationFact]
    public async Task ADurableMobCorpseCreationRunAgainIsRefusedByItsCorpse()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var corpseId = Guid.NewGuid();
        var operationId = ScopedGuid.Create("mob-corpse-create", corpseId);
        var request = new CreatePersistentMobCorpseRequest(
            operationId,
            corpseId,
            ItemScenarioWorld.WorkerId,
            ItemScenarioWorld.WorkerRuntimeId,
            ItemScenarioWorld.ShardId,
            "mob.feral_alpha",
            1.5d,
            0d,
            -2d,
            0d,
            0d,
            0d,
            1d,
            ItemScenarioWorld.MobCorpsePresentationKey,
            600d,
            [new MobCorpseLootEntryRequest(Guid.NewGuid(), "fixture.ore", 4)]);
        var created = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            request,
            CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await TrimOperationAsync(context, connection, operationId);

        var replay = await context.CorpseService.CreatePersistentMobCorpseAsync(
            ItemScenarioWorld.WorkerId,
            request,
            CancellationToken.None);

        Assert.False(replay.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.MobCorpseInvalid, replay.Error!.Code);
        Assert.Equal(0, await OperationCountAsync(connection, operationId));
        Assert.Equal(
            4,
            await connection.ExecuteScalarAsync<int>(
                """
                select coalesce(sum(item.quantity), 0)::integer
                from corpse_sections section
                join item_instances item on item.container_id = section.container_id
                where section.corpse_id = @CorpseId;
                """,
                new { CorpseId = corpseId }));
    }

    private static Task<ItemTransactionResult> ExpireRecoveryAsync(
        PostgresIntegrationTestContext context,
        Guid operationId,
        Guid deliveryId)
    {
        return context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ExpireRecoveryDeliveryCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new ExpireRecoveryDeliveryCommand(deliveryId)),
            CancellationToken.None);
    }

    private static Task<ItemTransactionResult> DieAsync(
        PostgresIntegrationTestContext context,
        Guid operationId,
        ProcessPlayerDeathCommand command)
    {
        return context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                command),
            CancellationToken.None);
    }

    // Moves the operation past a one-day window and runs a retention pass, which trims it.
    private static async Task TrimOperationAsync(
        PostgresIntegrationTestContext context,
        NpgsqlConnection connection,
        Guid operationId)
    {
        await connection.ExecuteAsync(
            """
            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '2 days'
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId });
        await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, await OperationCountAsync(connection, operationId));
    }

    private static Task<int> OperationCountAsync(NpgsqlConnection connection, Guid operationId)
    {
        return connection.ExecuteScalarAsync<int>(
            "select count(*) from item_operations where operation_id = @OperationId;",
            new { OperationId = operationId });
    }

    private static Task<int> DestructionCountAsync(NpgsqlConnection connection)
    {
        return connection.ExecuteScalarAsync<int>("select count(*) from item_destructions;");
    }
}
