using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Placement.Retention;

// Simulation sessions that ended: released, or unreleased with an expired lease, counted from the
// release or the expiry. A live session never ends before now, so it is never due. A session that a
// join ticket still names waits for the ticket, whose key would refuse the delete. A due row cannot
// change into a live one, and a join that releases an expired session first is seen by the lock's
// recheck of the row, so one statement may lock and decide.
internal sealed class SimulationSessionRetentionTask : IRetentionTask
{
    public string Name => "simulation_sessions";

    public RetentionClass Class => RetentionClass.SessionHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select simulation_session.id,
                       coalesce(simulation_session.released_at, simulation_session.expires_at) as ended_at
                from character_simulation_sessions simulation_session
                where coalesce(simulation_session.released_at, simulation_session.expires_at)
                      < now() - make_interval(secs => @WindowSeconds)
                  and not exists (
                      select 1
                      from simulation_join_tickets ticket
                      where ticket.simulation_session_id = simulation_session.id)
                order by coalesce(simulation_session.released_at, simulation_session.expires_at),
                         simulation_session.id
                limit @BatchSize
                for update of simulation_session skip locked
            ), deleted as (
                delete from character_simulation_sessions simulation_session
                using due
                where simulation_session.id = due.id
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
