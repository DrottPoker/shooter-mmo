using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Placement.Retention;

// Join tickets that ended: consumed, or unconsumed and expired, counted from the consume or the
// expiry. A pending ticket never ends before now, so it is never due, and an expired one can no
// longer be consumed.
internal sealed class JoinTicketRetentionTask : IRetentionTask
{
    public string Name => "join_tickets";

    public RetentionClass Class => RetentionClass.SessionHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select ticket.id, coalesce(ticket.consumed_at, ticket.expires_at) as ended_at
                from simulation_join_tickets ticket
                where coalesce(ticket.consumed_at, ticket.expires_at)
                      < now() - make_interval(secs => @WindowSeconds)
                order by coalesce(ticket.consumed_at, ticket.expires_at), ticket.id
                limit @BatchSize
                for update of ticket skip locked
            ), deleted as (
                delete from simulation_join_tickets ticket
                using due
                where ticket.id = due.id
                returning due.ended_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.ended_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
