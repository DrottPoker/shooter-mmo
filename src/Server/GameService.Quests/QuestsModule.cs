using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Quests.Persistence;
using Mmorpg.Server.Common.Quests;

namespace Mmorpg.GameService.Quests;

// Quests: the quest catalog that GameService runs, its mirrored revision and quest
// identities, and every character's quest log.
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
        services.AddScoped<QuestLogQueryService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapQuestEndpoints();
    }
}
