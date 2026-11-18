using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Placement;

internal readonly record struct PlacementShardState(
    string ShardId,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

// Shard availability and load as players see them in the shard list, derived from
// worker leases and simulation sessions, and the joins that a version mismatch refused. Shards
// are a small configured set, so the shard id is a label.
internal sealed class PlacementMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Placement";
    public const string ShardIdTag = "shard.id";
    public const string ReasonTag = "reason";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> joinRefusalCounter;
    private PlacementShardState[] shards = [];

    public PlacementMetrics()
    {
        joinRefusalCounter = meter.CreateCounter<long>(
            "game_service.placement.join.refusals");
        meter.CreateObservableGauge(
            "game_service.placement.shard.online",
            () => Observe(shard => shard.IsOnline ? 1 : 0));
        meter.CreateObservableGauge(
            "game_service.placement.shard.players",
            () => Observe(shard => shard.ActivePlayers));
        meter.CreateObservableGauge(
            "game_service.placement.shard.capacity",
            () => Observe(shard => shard.Capacity));
    }

    public void ObserveShards(IEnumerable<PlacementShardState> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Volatile.Write(ref shards, current.ToArray());
    }

    // A join that a version mismatch refused, by the Shard and the code that refused it, so an
    // operator sees how many clients need an update and on which Shard. The reason is one of the
    // codes of JoinVersionCheck.
    public void RecordJoinRefusal(string shardId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        joinRefusalCounter.Add(
            1,
            new KeyValuePair<string, object?>(ShardIdTag, shardId),
            new KeyValuePair<string, object?>(ReasonTag, reason));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<int>> Observe(Func<PlacementShardState, int> selector)
    {
        foreach (var shard in Volatile.Read(ref shards))
        {
            yield return new Measurement<int>(
                selector(shard),
                new KeyValuePair<string, object?>(ShardIdTag, shard.ShardId));
        }
    }
}
