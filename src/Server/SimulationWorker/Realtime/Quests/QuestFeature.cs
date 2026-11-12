using Microsoft.Extensions.DependencyInjection;
using Mmorpg.SimulationWorker.Quests;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.SimulationWorker.Realtime;

// What quests need beyond the realtime services: the quest logs of the active characters, the
// executor that runs quest actions through GameService, and the quest catalog that decides which
// kills and yields count. Without an executor no quest intent runs, and without the catalog no
// kill or yield counts for a quest.
internal sealed record QuestDependencies(
    CharacterQuestLogStore QuestLogs,
    QuestOperationExecutor? Executor,
    QuestCatalogRuntimeDocument? Catalog = null)
{
    // No quests: an empty quest log store, no executor, and no catalog.
    public static QuestDependencies CreateDefault()
    {
        return new QuestDependencies(new CharacterQuestLogStore(), null);
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new QuestDependencies(
            provider.GetRequiredService<CharacterQuestLogStore>(),
            provider.GetRequiredService<QuestOperationExecutor>(),
            provider.GetRequiredService<QuestCatalogRuntimeDocument>()));
    }
}

// What credits quests: the filter that tells whether a kill or a gathering yield counts, the
// roller of a dying Mob's quest drops, and the executor that commits quest actions. Its own unit
// because the combat authority and gathering credit quests before the quest intents, which need
// the combat authority, can exist.
internal sealed class QuestProgressFeature
{
    public QuestProgressFeature(QuestDependencies dependencies)
    {
        Executor = dependencies.Executor;
        if (dependencies.Catalog is not null)
        {
            Progress = new QuestProgressFilter(dependencies.QuestLogs, dependencies.Catalog);
            DropRoller = new QuestDropRoller(dependencies.QuestLogs, dependencies.Catalog);
        }
    }

    public QuestOperationExecutor? Executor { get; }

    public QuestProgressFilter? Progress { get; }

    public QuestDropRoller? DropRoller { get; }
}

// The quest actions outside any interaction: abandoning a quest and the admin quest tools.
internal static class QuestFeature
{
    public static void Add(
        RealtimeCoreServices core,
        QuestProgressFeature quests,
        PlayerCombatFeature playerCombat)
    {
        var intents = new QuestIntentHandler(
            core.Peers,
            core.Sender,
            core.CarrySync,
            core.Teardown,
            core.CompletionTargets,
            core.AuthorityScheduler,
            core.OperationTracker,
            core.Completions,
            quests.Executor,
            core.DevelopmentAdmin,
            core.Logger,
            playerCombat.Authority);
        QuestIntentHandler.AddRoute(core.Registries.Routes, intents.HandleIntent);
    }
}
