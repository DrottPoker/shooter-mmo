using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Integration;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Placement;

// Placement: topology, the worker registry, Shard placement, join tickets, simulation
// session leases, and Development simulation bots.
public sealed class PlacementModule(PlacementModuleOptions options) : IGameServiceModule
{
    public string Name => "Placement";

    public IReadOnlyList<DatabaseMigration> Migrations { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);
        services.AddSingleton(options.Worlds);
        services.AddSingleton(options.DevelopmentSimulationBots);
        services.AddSingleton(options.WorkerLeasePolicy);
        services.AddSingleton<IAccountSessionLifecycleHandler, SimulationAccountSessionLifecycleHandler>();
        services.AddSingleton<DevelopmentSimulationBotAuthority>();
        services.AddSingleton<SimulationTopologySeeder>();
        services.AddScoped<ShardService>();
        services.AddScoped<SimulationSessionService>();
        services.AddScoped<SimulationWorkerRegistryService>();
        services.AddScoped<DevelopmentSimulationBotPlacementService>();
        services.AddScoped<DevelopmentSimulationBotTicketService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSimulationEndpoints();
        endpoints.MapDevelopmentSimulationBotEndpoints(options.DevelopmentSimulationBots);
    }
}
