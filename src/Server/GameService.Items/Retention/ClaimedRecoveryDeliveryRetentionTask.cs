using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Items.Retention;

// Claimed Recovery deliveries, by the time they were claimed. A claimed delivery is history: its
// items already left Recovery Storage. An unclaimed one is never due here; Recovery expiry,
// which is game logic, ends it.
internal sealed class ClaimedRecoveryDeliveryRetentionTask : IRetentionTask
{
    public string Name => "claimed_recovery_deliveries";

    public RetentionClass Class => RetentionClass.ItemHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select delivery.id, delivery.claimed_at
                from recovery_deliveries delivery
                where delivery.claimed_at < now() - make_interval(secs => @WindowSeconds)
                order by delivery.claimed_at, delivery.id
                limit @BatchSize
                for update of delivery skip locked
            ), deleted as (
                delete from recovery_deliveries delivery
                using due
                where delivery.id = due.id
                returning due.claimed_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.claimed_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
