using System.Diagnostics.Metrics;
using System.Reflection;
using Mmorpg.GameService;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Server.Telemetry;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests;

public sealed class GameServiceTelemetryTests
{
    [Fact]
    public void EveryMeterIsDeclaredByTheModuleThatCreatesIt()
    {
        var configuration = TestConfiguration();
        var declaredByAssembly = GameServiceComposition.CreateModules(configuration)
            .ToDictionary(module => module.GetType().Assembly, module => module.MeterNames);
        var meterOwners = GameServiceAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => field.FieldType == typeof(Meter)))
            .ToArray();

        Assert.NotEmpty(meterOwners);
        foreach (var owner in meterOwners)
        {
            var meterName = owner.GetField("MeterName", BindingFlags.Public | BindingFlags.Static);
            Assert.True(
                meterName is { IsLiteral: true },
                $"{owner.FullName} must publish its meter name as a public const MeterName.");
            Assert.True(
                declaredByAssembly.TryGetValue(owner.Assembly, out var declared),
                $"{owner.FullName} creates a meter outside a module, so the host never exports it.");
            Assert.Contains((string)meterName!.GetRawConstantValue()!, declared);
        }

        Assert.Equal(
            meterOwners.Length + 1,
            GameServiceTelemetry.MeterNames(configuration).Count);
        Assert.Contains(
            GameServiceTelemetry.NpgsqlInstrumentationName,
            GameServiceTelemetry.MeterNames(configuration));
    }

    [Fact]
    public void DashboardsAndAlertsReferenceOnlyPublishedGameServiceMetrics()
    {
        var meterNames = GameServiceTelemetry.MeterNames(TestConfiguration());
        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name.StartsWith(
                        ServerTelemetry.ProjectInstrumentationPrefix,
                        StringComparison.Ordinal)
                    && meterNames.Contains(instrument.Meter.Name))
                {
                    lock (published)
                    {
                        published.UnionWith(ObservabilityAssets.PrometheusNames(instrument));
                    }
                }
            }
        };
        listener.Start();
        using var redisMetrics = new RedisAccelerationMetrics();
        using var itemMetrics = new ItemOperationsMetrics();
        using var placementMetrics = new PlacementMetrics();

        var referenced = ObservabilityAssets.ReferencedMetricNames("game_service_");

        Assert.NotEmpty(referenced);
        string[] unknown;
        lock (published)
        {
            unknown = referenced.Where(name => !published.Contains(name)).ToArray();
        }

        Assert.Equal([], unknown);
    }

    [Fact]
    public void InstanceIdDefaultsToTheHostNameAndCanBeOverridden()
    {
        Assert.Equal(
            Environment.MachineName.ToLowerInvariant(),
            GameServiceTelemetry.ResolveServiceInstanceId(TelemetryOptions.Disabled));
        Assert.Equal(
            "game-service-b",
            GameServiceTelemetry.ResolveServiceInstanceId(
                TelemetryOptions.Disabled with { ServiceInstanceId = "game-service-b" }));
    }

    private static GameServiceConfiguration TestConfiguration()
    {
        return GameServiceTestComposition.CreateConfiguration(
            "Host=localhost;Database=telemetry_test;Username=test;Password=test");
    }
}
