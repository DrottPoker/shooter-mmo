using AuthService.Items;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class ItemOperationsHardeningIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task OperationClaimLockTimeoutReturnsStableConflict()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "phase15-claim-lock@example.com",
            "phase15_claim_lock",
            "Phase 15 Claim Lock");
        var snapshot = await GetSnapshotAsync(context, player);
        var operationId = Guid.NewGuid();
        var options = ItemOperationsOptions.CreateDefaults() with
        {
            TransactionTimeout = TimeSpan.FromSeconds(2),
            LockTimeout = TimeSpan.FromMilliseconds(100)
        };
        using var metrics = new ItemOperationsMetrics();
        var service = new ItemTransactionService(context.DataSource, metrics, options);
        await using var blocker = await context.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            """
            insert into item_operations (
                operation_id,
                operation_kind,
                request_hash,
                request_payload)
            values (@OperationId, 'grant', @RequestHash, cast('{}' as jsonb));
            """,
            new
            {
                OperationId = operationId,
                RequestHash = new string('a', 64)
            },
            blockerTransaction);

        var result = await service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "material.iron_ore",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    snapshot.PermanentInventory.Slots.First(slot => slot.Item is null).SlotIndex)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, result.Error!.Code);
        Assert.Equal(1, metrics.Capture().Timeouts);
        await blockerTransaction.RollbackAsync();
    }

    [PostgresIntegrationFact]
    public async Task LockTimeoutReturnsStableConflictAndRecordsWaitMetrics()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "phase15-lock@example.com",
            "phase15_lock",
            "Phase 15 Lock");
        var snapshot = await GetSnapshotAsync(context, player);
        var options = ItemOperationsOptions.CreateDefaults() with
        {
            TransactionTimeout = TimeSpan.FromSeconds(2),
            LockTimeout = TimeSpan.FromMilliseconds(100)
        };
        using var metrics = new ItemOperationsMetrics();
        var service = new ItemTransactionService(context.DataSource, metrics, options);
        await using var blocker = await context.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "select character_id from character_item_states where character_id = @CharacterId for update;",
            new { CharacterId = player.Character.Id },
            blockerTransaction);

        var result = await service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "material.iron_ore",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    snapshot.PermanentInventory.Slots.First(slot => slot.Item is null).SlotIndex)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, result.Error!.Code);
        var metricSnapshot = metrics.Capture();
        Assert.Equal(1, metricSnapshot.Timeouts);
        Assert.True(metricSnapshot.LockWaitSamples >= 2);
        await blockerTransaction.RollbackAsync();
    }

    [PostgresIntegrationFact]
    public async Task MaintenanceExpiresRecoveryAndRemovesEligibleRetainedRowsWithoutLoss()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "phase15-maintenance@example.com",
            "phase15_maintenance",
            "Phase 15 Maintenance");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "material.iron_ore",
                    2,
                    snapshot.PermanentInventory.ContainerId,
                    snapshot.PermanentInventory.Slots.First(slot => slot.Item is null).SlotIndex)),
            CancellationToken.None);
        Assert.True(grant.Succeeded, grant.Error?.Message);
        var itemId = Assert.Single(grant.ItemRevisions).ItemInstanceId;
        snapshot = await GetSnapshotAsync(context, player);
        var deliveryOperationId = Guid.NewGuid();
        var delivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                deliveryOperationId,
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "phase15_expiry",
                    Guid.NewGuid().ToString("N"),
                    null,
                    DateTime.UtcNow.AddMinutes(5),
                    [new ItemRevisionExpectation(
                        itemId,
                        Assert.Single(grant.ItemRevisions).Revision)])),
            CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Error?.Message);
        var deliveryId = Assert.Single(delivery.RecoveryDeliveryIds);
        snapshot = await GetSnapshotAsync(context, player);
        var deathOperationId = Guid.NewGuid();
        var death = await context.CorpseService.ProcessSystemDeathAsync(
            deathOperationId,
            new ProcessPlayerDeathCommand(
                Guid.NewGuid(),
                player.Character.Id,
                snapshot.ItemStateRevision,
                "local-shard-1",
                0d,
                0d,
                0d,
                0d,
                0d,
                0d,
                1d,
                SimulationWorker.Corpses.MobCorpseLifecycleService.DefaultPresentationKey),
            CancellationToken.None);
        Assert.True(death.Succeeded, death.Error?.Message);
        var corpseId = death.Value!.Corpse.CorpseId;
        var rejectedOperationId = Guid.NewGuid();
        var rejected = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                rejectedOperationId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    null,
                    "missing.phase15.definition",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.False(rejected.Succeeded);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var corpseContainerIds = (await connection.QueryAsync<Guid>(
            "select container_id from corpse_sections where corpse_id = @CorpseId;",
            new { CorpseId = corpseId })).ToArray();
        await connection.ExecuteAsync(
            """
            update recovery_deliveries
            set created_at = now() - interval '2 seconds',
                expires_at = now() - interval '1 second'
            where id = @DeliveryId;
            """,
            new { DeliveryId = deliveryId });
        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '3 days',
                expires_at = now() - interval '3 days' + interval '5 minutes'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        Assert.Equal(
            1,
            await context.CorpseService.ExpireDueCorpsesAsync(8, CancellationToken.None));
        var expiryOperationId = await connection.ExecuteScalarAsync<Guid>(
            "select expiry_operation_id from corpses where id = @CorpseId;",
            new { CorpseId = corpseId });
        await connection.ExecuteAsync(
            """
            update corpses
            set closed_at = now() - interval '2 days'
            where id = @CorpseId;

            update item_operations
            set created_at = now() - interval '3 days',
                completed_at = now() - interval '2 days'
            where operation_id = any(@OperationIds);
            """,
            new
            {
                CorpseId = corpseId,
                OperationIds = new[]
                {
                    deathOperationId,
                    expiryOperationId,
                    rejectedOperationId
                }
            });

        var options = ItemOperationsOptions.CreateDefaults() with
        {
            AuditRetention = TimeSpan.FromDays(1),
            ClosedCorpseRetention = TimeSpan.FromDays(1),
            CleanupBatchSize = 64
        };
        using var metrics = new ItemOperationsMetrics();
        var transactionService = new ItemTransactionService(
            context.DataSource,
            metrics,
            options);
        var maintenance = new ItemOperationsMaintenanceService(
            context.DataSource,
            transactionService,
            options,
            metrics,
            NullLogger<ItemOperationsMaintenanceService>.Instance);

        var result = await maintenance.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiredRecoveryDeliveries);
        Assert.Equal(1, result.RemovedClosedCorpses);
        Assert.True(result.RemovedAuditOperations >= 1);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from recovery_deliveries where id = @DeliveryId;",
                new { DeliveryId = deliveryId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            "recovery_expired",
            await connection.ExecuteScalarAsync<string>(
                "select reason from item_destructions where item_instance_id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from corpses where id = @CorpseId;",
                new { CorpseId = corpseId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_containers where id = any(@ContainerIds);",
                new { ContainerIds = corpseContainerIds }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operations where operation_id = @OperationId;",
                new { OperationId = rejectedOperationId }));
        var metricSnapshot = metrics.Capture();
        Assert.Equal(1, metricSnapshot.ExpiredRecoveryDeliveries);
        Assert.Equal(1, metricSnapshot.RemovedClosedCorpses);
        Assert.True(metricSnapshot.RemovedAuditOperations >= 1);
    }

    private static async Task<CharacterInventorySnapshotResponse> GetSnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }
}
