using System.Diagnostics.Metrics;
using Mmorpg.GameService.Accounts.PasswordHashing;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Audit;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Tests.Architecture;

// The dashboards and alert rules in observability/ read these meter and instrument names,
// so moving the metric classes between modules must not rename them.
public sealed class MeterInstrumentNamesTests
{
    private static readonly string[] ExpectedInstruments =
    [
        "Mmorpg.GameService.Accounts|1.0.0|Counter`1|game_service.accounts.password_hashing.refusals|",
        "Mmorpg.GameService.Accounts|1.0.0|Histogram`1|game_service.accounts.password_hashing.duration|ms",
        "Mmorpg.GameService.Accounts|1.0.0|Histogram`1|game_service.accounts.password_hashing.wait.duration|ms",
        "Mmorpg.GameService.Accounts|1.0.0|ObservableGauge`1|game_service.accounts.password_hashing.in_flight|",
        "Mmorpg.GameService.Accounts|1.0.0|ObservableGauge`1|game_service.accounts.password_hashing.queue_length|",
        "Mmorpg.GameService.Audit|1.0.0|Counter`1|game_service.audit.runs|",
        "Mmorpg.GameService.Audit|1.0.0|ObservableGauge`1|game_service.audit.last_run_duration|s",
        "Mmorpg.GameService.Audit|1.0.0|ObservableGauge`1|game_service.audit.last_run_timestamp|s",
        "Mmorpg.GameService.Audit|1.0.0|ObservableGauge`1|game_service.audit.violations|",
        "Mmorpg.GameService.Items||Counter`1|game_service.items.cleanup|",
        "Mmorpg.GameService.Items||Counter`1|game_service.items.conflicts|",
        "Mmorpg.GameService.Items||Counter`1|game_service.items.death_partitions|",
        "Mmorpg.GameService.Items||Counter`1|game_service.items.policy_actions|",
        "Mmorpg.GameService.Items||Counter`1|game_service.items.transactions|",
        "Mmorpg.GameService.Items||Histogram`1|game_service.items.locks.wait.duration|ms",
        "Mmorpg.GameService.Items||Histogram`1|game_service.items.transactions.duration|ms",
        "Mmorpg.GameService.Items||ObservableGauge`1|game_service.items.corpses|",
        "Mmorpg.GameService.Items||ObservableGauge`1|game_service.items.recovery.backlog|",
        "Mmorpg.GameService.Placement||Counter`1|game_service.placement.character_states|",
        "Mmorpg.GameService.Placement||ObservableGauge`1|game_service.placement.sessions.expired_unreleased|",
        "Mmorpg.GameService.Placement||ObservableGauge`1|game_service.placement.shard.capacity|",
        "Mmorpg.GameService.Placement||ObservableGauge`1|game_service.placement.shard.online|",
        "Mmorpg.GameService.Placement||ObservableGauge`1|game_service.placement.shard.players|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|game_service.redis.authentication_rate_limit.operations|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|game_service.redis.failures|",
        "Mmorpg.GameService.Redis|1.0.0|Counter`1|game_service.redis.session_cache.operations|",
        "Mmorpg.GameService.Retention|1.0.0|Counter`1|game_service.retention.deleted|",
        "Mmorpg.GameService.Retention|1.0.0|Counter`1|game_service.retention.failures|",
        "Mmorpg.GameService.Retention|1.0.0|Histogram`1|game_service.retention.duration|ms",
        "Mmorpg.GameService.Retention|1.0.0|ObservableGauge`1|game_service.retention.lag|s"
    ];

    [Fact]
    public void GameServiceMetersKeepTheirNamesAndInstruments()
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name is "Mmorpg.GameService.Accounts"
                    or "Mmorpg.GameService.Audit"
                    or "Mmorpg.GameService.Items"
                    or "Mmorpg.GameService.Placement"
                    or "Mmorpg.GameService.Redis"
                    or "Mmorpg.GameService.Retention")
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

        using var passwordHashingMetrics = new PasswordHashingMetrics();
        using var redisMetrics = new RedisAccelerationMetrics();
        using var itemMetrics = new ItemOperationsMetrics();
        using var placementMetrics = new PlacementMetrics();
        using var retentionMetrics = new RetentionMetrics();
        using var auditMetrics = new AuditMetrics();

        string[] actual;
        lock (published)
        {
            actual = published.Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(ExpectedInstruments, actual);
    }
}
