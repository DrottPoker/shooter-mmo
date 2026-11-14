using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Placement.Retention;

// Released SimulationAssignments, counted from their release. The active assignment of a Shard is
// never released, so it is never due.
internal sealed class SimulationAssignmentRetentionTask : IRetentionTask
{
    public string Name => "simulation_assignments";

    public RetentionClass Class => RetentionClass.SessionHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            """
            with due as (
                select assignment.id, assignment.released_at
                from simulation_assignments assignment
                where assignment.released_at < now() - make_interval(secs => @WindowSeconds)
                order by assignment.released_at, assignment.id
                limit @BatchSize
                for update of assignment skip locked
            ), deleted as (
                delete from simulation_assignments assignment
                using due
                where assignment.id = due.id
                returning due.released_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.released_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
