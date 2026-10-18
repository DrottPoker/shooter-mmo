using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Mmorpg.Server.Telemetry;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Telemetry;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class SimulationWorkerTelemetryTests
{
    [Fact]
    public void EveryWorkerMeterIsExported()
    {
        var meterOwners = typeof(SimulationWorkerTelemetry).Assembly
            .GetTypes()
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
            Assert.Contains((string)meterName!.GetRawConstantValue()!, SimulationWorkerTelemetry.MeterNames);
        }

        Assert.Equal(meterOwners.Length, SimulationWorkerTelemetry.MeterNames.Count);
    }

    [Fact]
    public void DashboardsAndAlertsReferenceOnlyPublishedWorkerMetrics()
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (SimulationWorkerTelemetry.MeterNames.Contains(instrument.Meter.Name))
                {
                    lock (published)
                    {
                        published.UnionWith(ObservabilityAssets.PrometheusNames(instrument));
                    }
                }
            }
        };
        listener.Start();
        using var network = new RealtimeNetworkMetrics();
        using var performance = new RealtimePerformanceMetrics();
        using var worldActors = new WorldActorMetrics();
        using var corpses = new CorpseRuntimeMetrics();
        using var combat = new CombatMetrics();

        var referenced = ObservabilityAssets.ReferencedMetricNames("simulation_worker_");

        Assert.NotEmpty(referenced);
        string[] unknown;
        lock (published)
        {
            unknown = referenced.Where(name => !published.Contains(name)).ToArray();
        }

        Assert.Equal([], unknown);
    }

    [Fact]
    public void ResourceIdentifiesTheWorkerAndItsTopology()
    {
        var config = LoadConfig();
        var identity = SimulationWorkerIdentity.Create();

        var resource = SimulationWorkerTelemetry.CreateResource(
            config,
            identity,
            TelemetryOptions.Disabled,
            "Testing");
        var overridden = SimulationWorkerTelemetry.CreateResource(
            config,
            identity,
            TelemetryOptions.Disabled with { ServiceInstanceId = "worker-a" },
            "Testing");

        Assert.Equal(SimulationWorkerTelemetry.ServiceName, resource.ServiceName);
        Assert.Equal(config.SimulationWorkerId, resource.ServiceInstanceId);
        Assert.Equal("worker-a", overridden.ServiceInstanceId);
        var attributes = resource.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(config.FleetId, attributes["mmorpg.fleet.id"]);
        Assert.Equal(config.NodeId, attributes["mmorpg.node.id"]);
        Assert.Equal(config.ShardId, attributes["mmorpg.shard.id"]);
        Assert.Equal(config.WorldId, attributes["mmorpg.world.id"]);
        Assert.Equal(identity.RuntimeId, attributes["mmorpg.worker.runtime.id"]);
    }

    [Fact]
    public async Task GameServiceCallsAreSpansThatShareTheirTraceIdAsCorrelationId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SimulationWorkerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stopped)
                {
                    stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var correlationIds = new List<string>();
        var client = new GameServiceClient(new HttpClient(new ConflictHandler(correlationIds))
        {
            BaseAddress = new Uri("http://game-service.test")
        });

        var result = await client.ReleaseSimulationSessionAsync(
            Guid.NewGuid(),
            "runtime-1",
            "session-token",
            cancellationToken);

        Assert.False(result.Succeeded);
        Activity span;
        lock (stopped)
        {
            span = Assert.Single(stopped, activity =>
                activity.OperationName == "GameService.ReleaseSimulationSession");
        }

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("simulation_session_released", span.GetTagItem(SimulationWorkerTelemetry.ErrorCodeTag));
        Assert.Equal(span.TraceId.ToHexString(), Assert.Single(correlationIds));
    }

    private static SimulationWorkerConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(RepositoryPaths.Root)
            .AddJsonFile("src/Server/SimulationWorker/Config/appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SIMULATION_WORKER_SERVICE_SECRET"] =
                    "telemetry-unit-test-secret-with-at-least-32-characters",
                ["ConnectionStrings:Redis"] = "localhost:6379"
            })
            .Build();
        return SimulationWorkerConfig.FromConfiguration(configuration);
    }

    private sealed class ConflictHandler(List<string> correlationIds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            correlationIds.Add(request.Headers.GetValues("X-Correlation-ID").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new
                {
                    code = "simulation_session_released",
                    detail = "The simulation session was already released."
                })
            });
        }
    }
}
