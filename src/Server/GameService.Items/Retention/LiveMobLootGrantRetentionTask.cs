using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Items.Retention;

// Live Mob loot grants, by the time they were granted. A grant row keeps a live corpse's loot from
// being granted twice, which matters only while the worker holds that corpse, a few minutes at
// most, so the window must outlast every live corpse.
internal sealed class LiveMobLootGrantRetentionTask : IRetentionTask
{
    public string Name => "live_mob_loot_grants";

    public RetentionClass Class => RetentionClass.ItemHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select grant_row.grant_id, grant_row.granted_at
                from live_mob_loot_grants grant_row
                where grant_row.granted_at < now() - make_interval(secs => @WindowSeconds)
                order by grant_row.granted_at, grant_row.grant_id
                limit @BatchSize
                for update of grant_row skip locked
            ), deleted as (
                delete from live_mob_loot_grants grant_row
                using due
                where grant_row.grant_id = due.grant_id
                returning due.granted_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.granted_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
