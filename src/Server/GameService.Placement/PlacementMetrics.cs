using System.Diagnostics.Metrics;

namespace Mmorpg.GameService.Placement;

internal readonly record struct PlacementShardState(
    string ShardId,
    bool IsOnline,
    int ActivePlayers,
    int Capacity);

// Shard availability and load as players see them in the shard list, derived from
// worker leases and simulation sessions. Shards are a small configured set, so the
// shard id is the only label.
internal sealed class PlacementMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.GameService.Placement";
    public const string ShardIdTag = "shard.id";

    private readonly Meter meter = new(MeterName);
    private PlacementShardState[] shards = [];

    public PlacementMetrics()
    {
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
