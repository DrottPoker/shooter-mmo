using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed record ItemOperationsMaintenanceResult(
    int ExpiredRecoveryDeliveries,
    int RemovedClosedCorpses,
    int RemovedAuditOperations);

public sealed class ItemOperationsMaintenanceService(
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
        var (removedClosedCorpses, removedAuditOperations) =
            await RemoveRetainedRowsAsync(cancellationToken);
        metrics.RecordCleanup("recovery_delivery", expiredRecoveryDeliveries);
        metrics.RecordCleanup("closed_corpse", removedClosedCorpses);
        metrics.RecordCleanup("audit_operation", removedAuditOperations);
        return new ItemOperationsMaintenanceResult(
            expiredRecoveryDeliveries,
            removedClosedCorpses,
            removedAuditOperations);
    }

    private async Task<int> ExpireRecoveryDeliveriesAsync(
        CancellationToken cancellationToken)
    {
        Guid[] deliveryIds;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            deliveryIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select id
                from recovery_deliveries
                where claimed_at is null
                  and expires_at is not null
                  and expires_at <= now()
                order by expires_at, id
                limit @BatchSize;
                """,
                new { BatchSize = options.CleanupBatchSize },
                cancellationToken: cancellationToken))).ToArray();
        }
        var expiredCount = 0;
        foreach (var deliveryId in deliveryIds)
        {
            var operationId = CreateScopedGuid("recovery-expiry", deliveryId);
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

    private async Task<(int RemovedClosedCorpses, int RemovedAuditOperations)>
        RemoveRetainedRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select
                    set_config('statement_timeout', @StatementTimeout, true),
                    set_config('lock_timeout', @LockTimeout, true);
                """,
                new
                {
                    StatementTimeout = $"{options.TransactionTimeout.TotalMilliseconds:0}ms",
                    LockTimeout = $"{options.LockTimeout.TotalMilliseconds:0}ms"
                },
                transaction,
                cancellationToken: cancellationToken));
            var corpseIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                select corpse.id
                from corpses corpse
                where corpse.closed_at is not null
                  and corpse.closed_at <= now() - make_interval(secs => @RetentionSeconds)
                  and not exists (
                      select 1
                      from corpse_sections section
                      join item_instances item on item.container_id = section.container_id
                      where section.corpse_id = corpse.id)
                order by corpse.closed_at, corpse.id
                limit @BatchSize
                for update of corpse skip locked;
                """,
                new
                {
                    RetentionSeconds = options.ClosedCorpseRetention.TotalSeconds,
                    BatchSize = options.CleanupBatchSize
                },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            var containerIds = corpseIds.Length == 0
                ? []
                : (await connection.QueryAsync<Guid>(new CommandDefinition(
                    """
                    select container_id
                    from corpse_sections
                    where corpse_id = any(@CorpseIds)
                    order by container_id;
                    """,
                    new { CorpseIds = corpseIds },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();
            if (corpseIds.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from death_events where corpse_id = any(@CorpseIds);",
                    new { CorpseIds = corpseIds },
                    transaction,
                    cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from corpses where id = any(@CorpseIds);",
                    new { CorpseIds = corpseIds },
                    transaction,
                    cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    delete from item_containers
                    where id = any(@ContainerIds)
                      and lifecycle in ('closed', 'destroyed')
                      and not exists (
                          select 1 from item_instances where container_id = item_containers.id);
                    """,
                    new { ContainerIds = containerIds },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var removedAuditOperations = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    with candidates as (
                        select operation.operation_id
                        from item_operations operation
                        where operation.completed_at is not null
                          and operation.completed_at
                              <= now() - make_interval(secs => @RetentionSeconds)
                          and not exists (
                              select 1
                              from item_destructions destruction
                              where destruction.source_operation_id = operation.operation_id)
                          and not exists (
                              select 1
                              from death_events event
                              where event.operation_id = operation.operation_id)
                        order by operation.completed_at, operation.operation_id
                        limit @BatchSize
                        for update of operation skip locked
                    ), deleted as (
                        delete from item_operations operation
                        using candidates
                        where operation.operation_id = candidates.operation_id
                        returning operation.operation_id
                    )
                    select count(*)::integer from deleted;
                    """,
                    new
                    {
                        RetentionSeconds = options.AuditRetention.TotalSeconds,
                        BatchSize = options.CleanupBatchSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return (corpseIds.Length, removedAuditOperations);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static Guid CreateScopedGuid(string scope, Guid sourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            scope + ":" + sourceId.ToString("N")));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}

public sealed class ItemOperationsMaintenanceHostedService(
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
            if (result.ExpiredRecoveryDeliveries > 0
                || result.RemovedClosedCorpses > 0
                || result.RemovedAuditOperations > 0)
            {
                logger.LogInformation(
                    "Item operations maintenance expired {ExpiredRecoveryDeliveries} Recovery deliveries, removed {RemovedClosedCorpses} retained closed corpses, and removed {RemovedAuditOperations} retained audit operations.",
                    result.ExpiredRecoveryDeliveries,
                    result.RemovedClosedCorpses,
                    result.RemovedAuditOperations);
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
