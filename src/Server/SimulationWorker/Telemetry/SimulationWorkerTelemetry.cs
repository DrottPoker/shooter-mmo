using System.Diagnostics;
using Mmorpg.Server.Telemetry;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Telemetry;

// The worker's meters, its activity source, and its OpenTelemetry resource. Spans cover
// the asynchronous authority operations and their GameService calls, never the fixed
// tick, so tracing adds no work to the realtime loop.
public static class SimulationWorkerTelemetry
{
    public const string ServiceName = "simulation-worker";
    public const string ActivitySourceName = "Mmorpg.SimulationWorker";
    public const string ErrorCodeTag = "mmorpg.error.code";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    // Every meter the worker creates. A guard test fails when a metric class is added
    // without being listed here.
    public static IReadOnlyList<string> MeterNames { get; } =
    [
        RealtimeNetworkMetrics.MeterName,
        RealtimePerformanceMetrics.MeterName,
        WorldActorMetrics.MeterName,
        CorpseRuntimeMetrics.MeterName,
        CombatMetrics.MeterName
    ];

    public static TelemetryResource CreateResource(
        SimulationWorkerConfig config,
        SimulationWorkerIdentity identity,
        TelemetryOptions options,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);

        // The logical worker id survives restarts, so dashboards keep one series per
        // worker; the runtime id still tells the processes apart.
        return new TelemetryResource(
            ServiceName,
            TelemetryResource.ResolveServiceVersion(typeof(SimulationWorkerTelemetry).Assembly),
            options.ServiceInstanceId ?? config.SimulationWorkerId,
            environmentName,
            [
                new("mmorpg.fleet.id", config.FleetId),
                new("mmorpg.node.id", config.NodeId),
                new("mmorpg.shard.id", config.ShardId),
                new("mmorpg.world.id", config.WorldId),
                new("mmorpg.worker.runtime.id", identity.RuntimeId)
            ]);
    }

    // Returns null when no listener samples the span, so callers must accept null.
    public static Activity? StartOperation(string name)
    {
        return ActivitySource.StartActivity(name, ActivityKind.Internal);
    }

    public static void RecordFailure(Activity? activity, string code)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorCodeTag, code);
        activity.SetStatus(ActivityStatusCode.Error, code);
    }
}
