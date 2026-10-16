using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mmorpg.GameService.Placement.Tests;

public sealed class PlacementMetricsTests
{
    [Fact]
    public void GaugesReportEachShardByItsId()
    {
        using var metrics = new PlacementMetrics();
        metrics.ObserveShards(
        [
            new PlacementShardState("shard-a", IsOnline: true, ActivePlayers: 12, Capacity: 100),
            new PlacementShardState("shard-b", IsOnline: false, ActivePlayers: 0, Capacity: 0)
        ]);

        var measurements = Collect(metrics);

        Assert.Equal(1, measurements[("game_service.placement.shard.online", "shard-a")]);
        Assert.Equal(0, measurements[("game_service.placement.shard.online", "shard-b")]);
        Assert.Equal(12, measurements[("game_service.placement.shard.players", "shard-a")]);
        Assert.Equal(100, measurements[("game_service.placement.shard.capacity", "shard-a")]);
        Assert.Equal(6, measurements.Count);
    }

    [Fact]
    public async Task AFailedRefreshDropsTheShardsInsteadOfKeepingStaleValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var metrics = new PlacementMetrics();
        metrics.ObserveShards([new PlacementShardState("shard-a", true, 3, 100)]);
        var reporter = new PlacementMetricsReporterService(
            new FailingScopeFactory(),
            metrics,
            new PlacementModuleOptions(30, 60, TimeSpan.FromSeconds(30), new SimulationTopologyConfig([], [], [])),
            NullLogger<PlacementMetricsReporterService>.Instance);

        // The host runs ExecuteAsync in the background, so wait for the first refresh.
        await reporter.StartAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Collect(metrics).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        await reporter.StopAsync(cancellationToken);

        Assert.Empty(Collect(metrics));
    }

    private static Dictionary<(string Instrument, string ShardId), int> Collect(PlacementMetrics metrics)
    {
        var measurements = new Dictionary<(string, string), int>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PlacementMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var shardId = tags.ToArray()
                .Single(tag => tag.Key == PlacementMetrics.ShardIdTag)
                .Value as string;
            measurements[(instrument.Name, shardId!)] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        GC.KeepAlive(metrics);
        return measurements;
    }

    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The database is unavailable.");
    }
}
