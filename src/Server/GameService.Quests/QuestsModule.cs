using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Quests.Persistence;
using Mmorpg.GameService.Quests.Transactions.Domain;
using Mmorpg.GameService.Quests.Transactions.Handlers;
using Mmorpg.Server.Common.Quests;

namespace Mmorpg.GameService.Quests;

// Quests: the quest catalog that GameService runs, its mirrored revision and quest
// identities, every character's quest log, and the quest commands, which run in the item
// transaction kernel.
public sealed class QuestsModule(QuestsModuleOptions options) : IGameServiceModule
{
    public string Name => "Quests";

    public IReadOnlyList<DatabaseMigration> Migrations => QuestsSchemaMigrations.All;

    public IReadOnlyList<string> MeterNames { get; } = [];

    public void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The quest catalog is loaded once; a broken file stops the start.
        var questDefinitions = new QuestDefinitions(
            QuestCatalogFileLoader.Load(options.Catalog.RuntimeCatalogPath));
        services.AddSingleton(options);
        services.AddSingleton(options.Catalog);
        services.AddSingleton(questDefinitions);
        services.AddSingleton<IQuestDefinitions>(questDefinitions);
        services.AddSingleton<IDatabaseSeeder, QuestCatalogSeeder>();
        services.AddSingleton<ICharacterQuestLog, CharacterQuestLog>();
        services.AddSingleton<IQuestCatalogRevisionProvider, QuestCatalogRevisionProvider>();
        services.AddScoped<QuestLogQueryService>();
        services.AddScoped<SimulationQuestLogService>();
        AddQuestCommands(services);
    }

    // The quest commands register with Items' item command catalog, and the quest code
    // implements Items' quest drop demand, item catalog check, and gathering credit.
    private static void AddQuestCommands(IServiceCollection services)
    {
        services.AddSingleton<IItemCommandSource, QuestCommandSource>();
        services.AddSingleton<IQuestDropDemand, QuestDropDemand>();
        services.AddSingleton<IItemCatalogReferences, QuestItemCatalogReferences>();
        services.AddSingleton<IQuestGatherCredit, QuestGatherCredit>();
        services.AddScoped<SimulationQuestMutationService>();
        services.AddScoped<SimulationQuestDropService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapQuestEndpoints();
        endpoints.MapSimulationQuestEndpoints();
    }
}
