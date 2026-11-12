using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mmorpg.SimulationWorker.Realtime;

// Hosts the realtime UDP server. The realtime components are composed here from the
// injected dependency records and run on one loop strand; see RealtimeServer. It stops in
// StoppingAsync, before the host stops any hosted service, as
// SimulationWorkerHostedServices explains.
internal sealed class RealtimeSimulationService(
    RealtimeHostDependencies host,
    ItemDependencies items,
    CorpseDependencies corpses,
    WorldActorDependencies worldActors,
    CombatDependencies combat,
    AppearanceDependencies appearance,
    QuestDependencies quests,
    PersistenceDependencies persistence,
    ILogger<RealtimeSimulationService> logger)
    : BackgroundService, IHostedLifecycleService
{
    private readonly RealtimeServer server = RealtimeServerComposition.Create(
        new RealtimeServerDependencies(host, items, corpses, worldActors, combat, appearance, quests, persistence),
        logger);

    // The composed server, for the guard tests of what the features registered.
    internal RealtimeServer Server => server;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return server.RunAsync(stoppingToken);
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // The realtime shutdown runs here, so its combat flush and its last checkpoint commit while
    // the sessions and the registration still hold. The host's later StopAsync finds the loop
    // already stopped.
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return base.StopAsync(cancellationToken);
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        base.Dispose();
        server.Dispose();
    }
}
