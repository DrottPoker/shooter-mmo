using Mmorpg.GameService.Config;

namespace Mmorpg.GameService.Items;

// Items module composition: the item catalog, item state and transactions, corpses,
// policies, item operations maintenance, and the item development tools.
public static class ItemsModuleRegistration
{
    public static IServiceCollection AddItemsModule(
        this IServiceCollection services,
        ItemCatalogSource itemCatalogSource,
        StackStressFixtureOptions stackStressFixtureOptions,
        NpcItemLifecycleOptions npcItemLifecycleOptions,
        ItemOperationsOptions itemOperationsOptions)
    {
        ArgumentNullException.ThrowIfNull(itemCatalogSource);
        ArgumentNullException.ThrowIfNull(stackStressFixtureOptions);
        ArgumentNullException.ThrowIfNull(npcItemLifecycleOptions);
        ArgumentNullException.ThrowIfNull(itemOperationsOptions);

        services.AddSingleton(stackStressFixtureOptions);
        services.AddSingleton(npcItemLifecycleOptions);
        services.AddSingleton(itemOperationsOptions);
        services.AddSingleton(itemCatalogSource);
        services.AddSingleton<ItemOperationsMetrics>();
        services.AddSingleton<ItemCatalogSeeder>();
        services.AddSingleton<CharacterItemStateBootstrapper>();
        services.AddScoped<ItemCatalogQueryService>();
        services.AddScoped<ItemQueryService>();
        services.AddScoped<ItemTransactionService>();
        services.AddScoped<AccountItemMutationService>();
        services.AddScoped<SimulationItemMutationService>();
        services.AddScoped<ItemPolicyService>();
        services.AddScoped<QuestItemService>();
        services.AddScoped<CorpseService>();
        services.AddScoped<ItemOperationsMaintenanceService>();
        services.AddScoped<PhaseNineDevelopmentFixtureSeeder>();
        services.AddScoped<DevelopmentItemToolService>();
        services.AddScoped<StackStressFixtureService>();
        services.AddHostedService<CorpseExpiryHostedService>();
        services.AddHostedService<ItemOperationsMetricsReporterService>();
        services.AddHostedService<ItemOperationsMaintenanceHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapItemsModule(
        this IEndpointRouteBuilder app,
        StackStressFixtureOptions stackStressFixtureOptions)
    {
        app.MapItemEndpoints();
        app.MapSimulationItemEndpoints();
        app.MapCorpseEndpoints();
        app.MapStackStressFixtureEndpoints(stackStressFixtureOptions);
        return app;
    }
}
