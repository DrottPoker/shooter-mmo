using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Placement.Integration;
using Mmorpg.GameService.Placement.Persistence;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Placement;

// Placement: topology, the worker registry, Shard placement, join tickets, simulation
// session leases, and Development simulation bots.
public sealed class PlacementModule(PlacementModuleOptions options) : IGameServiceModule
{
    public string Name => "Placement";

    public IReadOnlyList<DatabaseMigration> Migrations => PlacementSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [PlacementMetrics.MeterName];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options);
        services.AddSingleton(options.Worlds);
        services.AddSingleton(options.DevelopmentSimulationBots);
        services.AddSingleton(options.WorkerLeasePolicy);
        services.AddSingleton<IAccountSessionLifecycleHandler, SimulationAccountSessionLifecycleHandler>();
        services.AddSingleton<ISimulationSessionAuthority, SimulationSessionAuthority>();
        services.AddSingleton<ISimulationWorkerAuthority, SimulationWorkerAuthority>();
        services.AddSingleton<ISimulationPresence, SimulationPresence>();
        services.AddSingleton<DevelopmentSimulationBotAuthority>();
        services.AddSingleton<SimulationTopologySeeder>();
        services.AddSingleton<PlacementMetrics>();
        services.AddScoped<ShardService>();
        services.AddScoped<SimulationSessionService>();
        services.AddScoped<SimulationAppearanceService>();
        services.AddScoped<SimulationWorkerRegistryService>();
        services.AddScoped<DevelopmentSimulationBotPlacementService>();
        services.AddScoped<DevelopmentSimulationBotTicketService>();
        services.AddHostedService<PlacementMetricsReporterService>();
    }

    // Ensures the configured World definitions, Fleets, Nodes, and Shards exist. The host
    // runs it after the database initializer and before it accepts requests.
    public static Task SeedTopologyAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<SimulationTopologySeeder>().SeedAsync(cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSimulationEndpoints();
        endpoints.MapDevelopmentSimulationBotEndpoints(options.DevelopmentSimulationBots);
    }
}
