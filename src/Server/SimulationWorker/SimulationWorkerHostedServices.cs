using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Health;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker;

public static class SimulationWorkerHostedServices
{
    // Registers the hosted services in their start order. The realtime service starts first, so
    // the worker binds its UDP port before anything else runs, and a port it cannot bind fails
    // the start before the worker registers. The worker heartbeat registers once the transport
    // listens, and corpse restoration waits for the registration lease that heartbeat renews. The
    // outage monitor stops the worker when no heartbeat succeeded for the outage tolerance.
    //
    // The host stops hosted services in reverse order, so the worker heartbeat sends /offline
    // and the session heartbeat then releases every session. The realtime service therefore
    // stops earlier, in IHostedLifecycleService.StoppingAsync, which the host calls before it
    // stops any hosted service: its shutdown commits the uncommitted arrow shots and quest
    // credits and saves the place and vitals of every remaining player while the sessions and
    // the registration still hold.
    // The host stops every hosted service within one shutdown timeout, which would otherwise be the
    // host's default of 30 seconds. The orderly stop can spend one call timeout on each of its steps
    // that call GameService, so the worker sets the timeout from them, as SimulationWorkerConfig
    // validates, and a container's stop grace period must stay longer than it.
    public static IServiceCollection AddSimulationWorkerShutdownTimeout(
        this IServiceCollection services,
        SimulationWorkerConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        services.Configure<HostOptions>(options => options.ShutdownTimeout = config.ShutdownTimeout);
        return services;
    }

    public static IServiceCollection AddSimulationWorkerHostedServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RealtimeSimulationService>();
        services.AddHostedService<SimulationSessionHeartbeatService>();
        services.AddHostedService<SimulationWorkerHeartbeatService>();
        services.AddHostedService<GameServiceOutageMonitor>();
        services.AddHostedService<DurableCorpseRestorationService>();
        services.AddHostedService<RealtimeMetricsReporterService>();
        services.AddHostedService<SimulationWorkerHealthFileService>();
        return services;
    }
}
