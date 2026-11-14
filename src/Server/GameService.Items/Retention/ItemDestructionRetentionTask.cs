using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Items.Retention;

// Item destructions, by the time the item was destroyed. A destruction names its operation without
// a foreign key, so the two are trimmed apart.
internal sealed class ItemDestructionRetentionTask : IRetentionTask
{
    public string Name => "item_destructions";

    public RetentionClass Class => RetentionClass.ItemHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select destruction.item_instance_id, destruction.destroyed_at
                from item_destructions destruction
                where destruction.destroyed_at < now() - make_interval(secs => @WindowSeconds)
                order by destruction.destroyed_at, destruction.item_instance_id
                limit @BatchSize
                for update of destruction skip locked
            ), deleted as (
                delete from item_destructions destruction
                using due
                where destruction.item_instance_id = due.item_instance_id
                returning due.destroyed_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.destroyed_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
