using Mmorpg.GameService.Config;

namespace Mmorpg.GameService.Simulation;

// Placement module composition: topology, the worker registry, Shard placement,
// join tickets, simulation session leases, and Development simulation bots.
public static class PlacementModuleRegistration
{
    public static IServiceCollection AddPlacementModule(
        this IServiceCollection services,
        WorldManifestCatalog worldManifestCatalog,
        DevelopmentSimulationBotOptions developmentSimulationBotOptions)
    {
        ArgumentNullException.ThrowIfNull(worldManifestCatalog);
        ArgumentNullException.ThrowIfNull(developmentSimulationBotOptions);

        services.AddSingleton(worldManifestCatalog);
        services.AddSingleton(developmentSimulationBotOptions);
        services.AddSingleton<DevelopmentSimulationBotAuthority>();
        services.AddSingleton<SimulationTopologySeeder>();
        services.AddScoped<ShardService>();
        services.AddScoped<SimulationSessionService>();
        services.AddScoped<SimulationWorkerRegistryService>();
        services.AddScoped<DevelopmentSimulationBotPlacementService>();
        services.AddScoped<DevelopmentSimulationBotTicketService>();
        return services;
    }

    public static IEndpointRouteBuilder MapPlacementModule(
        this IEndpointRouteBuilder app,
        DevelopmentSimulationBotOptions developmentSimulationBotOptions)
    {
        app.MapSimulationEndpoints();
        app.MapDevelopmentSimulationBotEndpoints(developmentSimulationBotOptions);
        return app;
    }
}
