using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed class ItemOperationsMetricsReporterService(
    NpgsqlDataSource dataSource,
    ItemOperationsMetrics metrics,
    ItemOperationsOptions options,
    ILogger<ItemOperationsMetricsReporterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.MetricsInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var state = await connection.QuerySingleAsync<OperationalState>(new CommandDefinition(
                """
                select
                    (select count(*) from corpses where closed_at is null) as "OpenCorpses",
                    (select count(*) from corpses where closed_at is not null) as "ClosedCorpses",
                    (
                        select count(*)
                        from recovery_deliveries
                        where claimed_at is null
                          and (available_at is null or available_at <= now())
                          and (expires_at is null or expires_at > now())
                    ) as "RecoveryBacklog";
                """,
                cancellationToken: cancellationToken));
            metrics.ObserveOperationalState(
                state.OpenCorpses,
                state.ClosedCorpses,
                state.RecoveryBacklog);
            var snapshot = metrics.Capture();
            logger.LogInformation(
                "Item operations status: transactions {Transactions}, committed {CommittedTransactions}, rejected {RejectedTransactions}, conflicts {Conflicts}, stale revisions {StaleRevisions}, timeouts {Timeouts}, lock samples {LockWaitSamples}, death partitions {DeathPartitions}, policy actions {PolicyActions}, open corpses {OpenCorpses}, closed corpses {ClosedCorpses}, Recovery backlog {RecoveryBacklog}, expired Recovery cleanup {ExpiredRecoveryDeliveries}, closed corpse cleanup {RemovedClosedCorpses}, audit cleanup {RemovedAuditOperations}.",
                snapshot.Transactions,
                snapshot.CommittedTransactions,
                snapshot.RejectedTransactions,
                snapshot.Conflicts,
                snapshot.StaleRevisions,
                snapshot.Timeouts,
                snapshot.LockWaitSamples,
                snapshot.DeathPartitions,
                snapshot.PolicyActions,
                snapshot.OpenCorpses,
                snapshot.ClosedCorpses,
                snapshot.RecoveryBacklog,
                snapshot.ExpiredRecoveryDeliveries,
                snapshot.RemovedClosedCorpses,
                snapshot.RemovedAuditOperations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Item operations metrics refresh failed.");
        }
    }

    private sealed class OperationalState
    {
        public long OpenCorpses { get; set; }

        public long ClosedCorpses { get; set; }

        public long RecoveryBacklog { get; set; }
    }
}
