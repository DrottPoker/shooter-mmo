namespace Mmorpg.GameService.Items;

// Items module composition: the item catalog, item state and transactions, corpses,
// policies, item operations maintenance, and the item development tools.
public static class ItemsModuleRegistration
{
    public static IServiceCollection AddItemsModule(
        this IServiceCollection services,
        ItemsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(options.StackStressFixtures);
        services.AddSingleton(options.NpcLifecycle);
        services.AddSingleton(options.Operations);
        services.AddSingleton(options.Catalog);
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
        ItemsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        app.MapItemEndpoints();
        app.MapSimulationItemEndpoints();
        app.MapCorpseEndpoints();
        app.MapStackStressFixtureEndpoints(options.StackStressFixtures);
        return app;
    }
}
