using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.Server.Common.Identity;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed record ItemOperationsMaintenanceResult(int ExpiredRecoveryDeliveries);

// Recovery expiry, which is game logic: an unclaimed delivery past its expiry has its items
// destroyed through a system operation. Trimming history is the retention tasks' work, as
// docs/GAME_SERVICE_MODULES.md describes under Retention.
internal sealed class ItemOperationsMaintenanceService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    ItemOperationsOptions options,
    ItemOperationsMetrics metrics,
    ILogger<ItemOperationsMaintenanceService> logger)
{
    public async Task<ItemOperationsMaintenanceResult> RunOnceAsync(
        CancellationToken cancellationToken)
    {
        var expiredRecoveryDeliveries = await ExpireRecoveryDeliveriesAsync(
            cancellationToken);
        metrics.RecordCleanup("recovery_delivery", expiredRecoveryDeliveries);
        return new ItemOperationsMaintenanceResult(expiredRecoveryDeliveries);
    }

    private async Task<int> ExpireRecoveryDeliveriesAsync(
        CancellationToken cancellationToken)
    {
        Guid[] deliveryIds;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            deliveryIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                $"""
                select delivery.id
                from recovery_deliveries delivery
                -- Expiry cannot lock a deleted character, so its rows must not hold the batch.
                join {CharactersSql.ActiveCharacters} character on character.id = delivery.character_id
                where delivery.claimed_at is null
                  and delivery.expires_at is not null
                  and delivery.expires_at <= now()
                order by delivery.expires_at, delivery.id
                limit @BatchSize;
                """,
                new { BatchSize = options.CleanupBatchSize },
                cancellationToken: cancellationToken))).ToArray();
        }
        var expiredCount = 0;
        foreach (var deliveryId in deliveryIds)
        {
            var operationId = ScopedGuid.Create("recovery-expiry", deliveryId);
            var result = await transactionService.ExecuteAsync(
                new ItemTransactionRequest<ExpireRecoveryDeliveryCommand>(
                    operationId,
                    ItemTransactionActor.ForSystem(),
                    new ExpireRecoveryDeliveryCommand(deliveryId)),
                cancellationToken);
            if (result.Succeeded)
            {
                expiredCount++;
                continue;
            }

            if (result.Error?.Code is ItemTransactionErrorCodes.RecoveryDeliveryNotFound
                or ItemTransactionErrorCodes.RecoveryDeliveryNotExpired)
            {
                continue;
            }

            logger.LogWarning(
                "Recovery expiry operation {OperationId} was rejected with {ErrorCode}.",
                operationId,
                result.Error?.Code ?? "item_transaction_rejected");
        }

        return expiredCount;
    }
}

internal sealed class ItemOperationsMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ItemOperationsOptions options,
    ILogger<ItemOperationsMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ItemOperationsMaintenanceService>();
            var result = await service.RunOnceAsync(cancellationToken);
            if (result.ExpiredRecoveryDeliveries > 0)
            {
                logger.LogInformation(
                    "Item operations maintenance expired {ExpiredRecoveryDeliveries} Recovery deliveries.",
                    result.ExpiredRecoveryDeliveries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Item operations maintenance failed.");
        }
    }
}
