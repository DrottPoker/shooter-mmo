namespace Mmorpg.GameService.Simulation;

// Placement module composition: topology, the worker registry, Shard placement,
// join tickets, simulation session leases, and Development simulation bots.
public static class PlacementModuleRegistration
{
    public static IServiceCollection AddPlacementModule(
        this IServiceCollection services,
        PlacementModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(options.Worlds);
        services.AddSingleton(options.DevelopmentSimulationBots);
        services.AddSingleton(options.WorkerLeasePolicy);
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
        PlacementModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        app.MapSimulationEndpoints();
        app.MapDevelopmentSimulationBotEndpoints(options.DevelopmentSimulationBots);
        return app;
    }
}
