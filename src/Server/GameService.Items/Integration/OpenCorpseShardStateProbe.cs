using Dapper;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Items.Integration;

// Open durable corpses belong to their Shard's World, so they block a World rebind.
public sealed class OpenCorpseShardStateProbe : IShardWorldLocalStateProbe
{
    public Task<long> CountOpenDurableCorpsesAsync(
        PostgresTransactionScope scope,
        string shardId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            select count(*)
            from corpses
            where shard_id = @ShardId
              and closed_at is null;
            """,
            new { ShardId = shardId },
            scope.Transaction,
            cancellationToken: cancellationToken));
    }
}
