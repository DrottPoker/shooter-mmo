using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Items.Integration;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Items.StackStress;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Handlers;
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
        services.AddSingleton<IItemCatalogRevisionProvider, ItemCatalogRevisionProvider>();
        services.AddScoped<ItemQueryService>();
        AddItemTransactions(services);
        services.AddScoped<AccountItemMutationService>();
        services.AddSingleton<SimulationCharacterStates>();
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

    // The item transaction kernel and its parts are stateless and shared, so they are
    // singletons; each IItemCommandSource registers the commands of one area, also the
    // sources of the modules above Items. Those modules also implement IQuestDropDemand,
    // IItemCatalogReferences, and IQuestGatherCredit, which Items' corpse rules, seeder, and
    // gathering command call.
    private static void AddItemTransactions(IServiceCollection services)
    {
        services.AddSingleton<ItemSimulationAuthority>();
        services.AddSingleton<CharacterItemStateLocks>();
        services.AddSingleton<ItemOperationLedger>();
        services.AddSingleton<ItemTransactionTelemetry>();
        services.AddSingleton<ItemTransactionKernel>();
        services.AddSingleton<IItemCommandSource, CoreItemCommandSource>();
        services.AddSingleton<IItemCommandSource, RecoveryAndPolicyCommandSource>();
        services.AddSingleton<IItemCommandSource, CorpseCommandSource>();
        services.AddSingleton<IItemCommandSource, EconomyCommandSource>();
        services.AddSingleton<ItemCommandCatalog>();
        services.AddScoped<ItemTransactionService>();
        services.AddScoped<IItemTransactions>(provider => provider.GetRequiredService<ItemTransactionService>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapItemEndpoints();
        endpoints.MapSimulationItemEndpoints();
        endpoints.MapCorpseEndpoints();
        endpoints.MapStackStressFixtureEndpoints(options.StackStressFixtures);
    }
}
