using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Placement.Contracts;

// Durable state that other modules keep per Shard and that ties the Shard to its
// current World. Placement refuses a World rebind while any of it is open. Exactly one
// implementation is registered.
public interface IShardWorldLocalStateProbe
{
    // Counts open durable corpses on the Shard in the caller's transaction. The caller
    // holds the Shard row lock, which blocks new corpses for that Shard.
    Task<long> CountOpenDurableCorpsesAsync(
        PostgresTransactionScope scope,
        string shardId,
        CancellationToken cancellationToken);
}
