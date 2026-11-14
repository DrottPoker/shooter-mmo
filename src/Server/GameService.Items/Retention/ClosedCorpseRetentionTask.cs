using Dapper;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Items.Retention;

// Closed corpses with their death events, sections, snapshots, and closed section containers, by
// the time the corpse closed. The death event goes first, because its key holds the corpse, and
// its operation becomes due for ItemOperationRetentionTask once it is gone. A corpse that still
// holds an item stays.
internal sealed class ClosedCorpseRetentionTask : IRetentionTask
{
    public string Name => "closed_corpses";

    public RetentionClass Class => RetentionClass.ItemHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var connection = scope.Connection;
        var transaction = scope.Transaction;

        // A statement that waits for a row lock still reads with the snapshot from before the
        // wait, so the corpses are locked in one statement and read again in the next. Skip locked
        // never waits, but the second read keeps the decision on the locked rows.
        var locked = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select corpse.id
            from corpses corpse
            where corpse.closed_at < now() - make_interval(secs => @WindowSeconds)
              and not exists (
                  select 1
                  from corpse_sections section
                  join item_instances item on item.container_id = section.container_id
                  where section.corpse_id = corpse.id)
            order by corpse.closed_at, corpse.id
            limit @BatchSize
            for update of corpse skip locked;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (locked.Length == 0)
        {
            return RetentionBatchResult.None;
        }

        var empty = (await connection.QueryAsync<EmptyCorpseRow>(new CommandDefinition(
            """
            select
                corpse.id as "CorpseId",
                extract(epoch from now() - corpse.closed_at)::double precision as "AgeSeconds"
            from corpses corpse
            where corpse.id = any(@CorpseIds)
              and not exists (
                  select 1
                  from corpse_sections section
                  join item_instances item on item.container_id = section.container_id
                  where section.corpse_id = corpse.id);
            """,
            new { CorpseIds = locked },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (empty.Length == 0)
        {
            return RetentionBatchResult.None;
        }

        var corpseIds = empty.Select(corpse => corpse.CorpseId).ToArray();
        var containerIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select container_id
            from corpse_sections
            where corpse_id = any(@CorpseIds)
            order by container_id;
            """,
            new { CorpseIds = corpseIds },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from death_events
            where corpse_id = any(@CorpseIds);

            delete from corpses
            where id = any(@CorpseIds);

            delete from item_containers container
            where container.id = any(@ContainerIds)
              and container.lifecycle = 'closed'
              and not exists (
                  select 1
                  from item_instances item
                  where item.container_id = container.id);
            """,
            new { CorpseIds = corpseIds, ContainerIds = containerIds },
            transaction,
            cancellationToken: cancellationToken));
        return new RetentionBatchResult(
            corpseIds.Length,
            TimeSpan.FromSeconds(empty.Max(corpse => corpse.AgeSeconds)));
    }

    private sealed class EmptyCorpseRow
    {
        public Guid CorpseId { get; set; }

        public double AgeSeconds { get; set; }
    }
}
