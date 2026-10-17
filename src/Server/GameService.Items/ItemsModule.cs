using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Items.Integration;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Items.StackStress;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Items;

// Items: the item catalog, item state and transactions, corpses, policies, item
// operations maintenance, the Development item tools, and the stack stress fixtures.
public sealed class ItemsModule(ItemsModuleOptions options) : IGameServiceModule
{
    public string Name => "Items";

    public IReadOnlyList<DatabaseMigration> Migrations => ItemsSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [ItemOperationsMetrics.MeterName];

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
        services.AddSingleton<IDatabaseSeeder, ItemsDatabaseSeeder>();
        services.AddSingleton<ICharacterLifecycleHandler, CharacterItemStateLifecycleHandler>();
        services.AddSingleton<ISimulationCharacterStateProvider, SimulationCharacterStateProvider>();
        services.AddSingleton<IShardWorldLocalStateProbe, OpenCorpseShardStateProbe>();
        services.AddScoped<ItemCatalogQueryService>();
        services.AddScoped<ItemQueryService>();
        services.AddScoped<ItemTransactionService>();
        services.AddScoped<AccountItemMutationService>();
        services.AddScoped<SimulationItemMutationService>();
        services.AddScoped<CorpseService>();
        services.AddScoped<ItemOperationsMaintenanceService>();
        services.AddSingleton(options.DevelopmentItemPackages);
        services.AddScoped<DevelopmentItemPackageApplier>();
        services.AddScoped<DevelopmentItemToolService>();
        if (options.StackStressFixtures.Enabled)
        {
            services.AddScoped<StackStressFixtureService>();
        }

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
