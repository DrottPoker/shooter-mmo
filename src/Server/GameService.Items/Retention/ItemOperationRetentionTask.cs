using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Items.Retention;

// Item operations with their journal rows, which go by cascade. An operation is due once it
// completed longer ago than the window; a pending one never is. The operation of a death waits
// until its death event left with its corpse, because the event's key holds it. What the statement
// decides on never changes once a row is due: a completed operation stays completed and a death
// event only ever leaves, so one statement may lock and decide.
internal sealed class ItemOperationRetentionTask : IRetentionTask
{
    public string Name => "item_operations";

    public RetentionClass Class => RetentionClass.ItemHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select operation.operation_id, operation.completed_at
                from item_operations operation
                where operation.completed_at < now() - make_interval(secs => @WindowSeconds)
                  and not exists (
                      select 1
                      from death_events event
                      where event.operation_id = operation.operation_id)
                order by operation.completed_at, operation.operation_id
                limit @BatchSize
                for update of operation skip locked
            ), deleted as (
                delete from item_operations operation
                using due
                where operation.operation_id = due.operation_id
                returning due.completed_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.completed_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
