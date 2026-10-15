using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Items;

// Items: the item catalog, item state and transactions, corpses, policies, item
// operations maintenance, and the item development tools.
public sealed class ItemsModule(ItemsModuleOptions options) : IGameServiceModule
{
    public string Name => "Items";

    public IReadOnlyList<DatabaseMigration> Migrations { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

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
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapItemEndpoints();
        endpoints.MapSimulationItemEndpoints();
        endpoints.MapCorpseEndpoints();
        endpoints.MapStackStressFixtureEndpoints(options.StackStressFixtures);
    }
}
