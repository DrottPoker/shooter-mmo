using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker;

public static class SimulationWorkerHostedServices
{
    // Registers the hosted services in their start order. The realtime service starts first, so
    // the worker binds its UDP port before anything else runs, and a port it cannot bind fails
    // the start before the worker registers. The worker heartbeat registers once the transport
    // listens, and corpse restoration waits for the registration lease that heartbeat renews.
    //
    // The host stops hosted services in reverse order, so the worker heartbeat sends /offline
    // and the session heartbeat then releases every session. The realtime service therefore
    // stops earlier, in IHostedLifecycleService.StoppingAsync, which the host calls before it
    // stops any hosted service: its shutdown commits the uncommitted arrow shots and quest
    // credits while the sessions and the registration still hold.
    public static IServiceCollection AddSimulationWorkerHostedServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RealtimeSimulationService>();
        services.AddHostedService<SimulationSessionHeartbeatService>();
        services.AddHostedService<SimulationWorkerHeartbeatService>();
        services.AddHostedService<SimulationWorkerRegistrationLeaseMonitor>();
        services.AddHostedService<DurableCorpseRestorationService>();
        services.AddHostedService<RealtimeMetricsReporterService>();
        return services;
    }
}
