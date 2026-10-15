using System.Diagnostics.Metrics;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Items;

namespace Mmorpg.Backend.Tests.Architecture;

// Dashboards and alerts read these meter and instrument names, so moving the metric
// classes between modules must not rename them.
public sealed class MeterInstrumentNamesTests
{
    private static readonly string[] ExpectedInstruments =
    [
        "Mmorpg.GameService.Items||Counter`1|auth_service.items.cleanup|",
        "Mmorpg.GameService.Items||Counter`1|auth_service.items.conflicts|",
        "Mmorpg.GameService.Items||Counter`1|auth_service.items.death_partitions|",
        "Mmorpg.GameService.Items||Counter`1|auth_service.items.policy_actions|",
        "Mmorpg.GameService.Items||Counter`1|auth_service.items.transactions|",
        "Mmorpg.GameService.Items||Histogram`1|auth_service.items.locks.wait.duration|ms",
        "Mmorpg.GameService.Items||Histogram`1|auth_service.items.transactions.duration|ms",
        "Mmorpg.GameService.Items||ObservableGauge`1|auth_service.items.corpses|",
        "Mmorpg.GameService.Items||ObservableGauge`1|auth_service.items.recovery.backlog|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|auth_service.redis.authentication_rate_limit.operations|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|auth_service.redis.failures|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|auth_service.redis.session_cache.operations|"
    ];

    [Fact]
    public void GameServiceMetersKeepTheirNamesAndInstruments()
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name is "Mmorpg.GameService.Items" or "Mmorpg.GameService.Redis")
                {
                    lock (published)
                    {
                        published.Add(string.Join(
                            '|',
                            instrument.Meter.Name,
                            instrument.Meter.Version,
                            instrument.GetType().Name,
                            instrument.Name,
                            instrument.Unit));
                    }
                }
            }
        };
        listener.Start();

        using var redisMetrics = new RedisAccelerationMetrics();
        using var itemMetrics = new ItemOperationsMetrics();

        string[] actual;
        lock (published)
        {
            actual = published.Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(ExpectedInstruments, actual);
    }
}
