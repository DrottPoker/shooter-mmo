using Mmorpg.GameService.Economy.Transactions.Handlers;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;

namespace Mmorpg.GameService.Economy;

// Economy: NPC vendor purchases and sales and gathering yields. They are item commands of Items'
// item transaction kernel and operation kinds of Items' worker item route, and a gathering yield
// counts for quests through Quests. Economy owns no table, migration, meter, route, or setting:
// the vendor profiles and tool kinds are part of Items' item catalog.
public sealed class EconomyModule : IGameServiceModule
{
    public string Name => "Economy";

    public IReadOnlyList<DatabaseMigration> Migrations { get; } = [];

    public IReadOnlyList<string> MeterNames { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IItemCommandSource, EconomyCommandSource>();
        services.AddSingleton<ISimulationItemOperationHandler, VendorPurchaseOperationHandler>();
        services.AddSingleton<ISimulationItemOperationHandler, VendorSaleOperationHandler>();
        services.AddSingleton<ISimulationItemOperationHandler, GatherResourceOperationHandler>();
    }

    // The vendor and gathering operations arrive on Items' worker item route.
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
    }
}
