using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Transactions.Domain;

// The quest side of quest drops: a drop counts while its quest is active and the quest's current
// step holds the collect objective for the item with the drop's source as a drop source. The
// objective's count is what the quest asks for in total.
internal sealed class QuestDropDemand(
    IQuestDefinitions questDefinitions,
    ICharacterQuestLog questLog) : IQuestDropDemand
{
    private readonly IQuestDefinitions questDefinitions = questDefinitions;
    private readonly ICharacterQuestLog questLog = questLog;

    public async Task<int> WantedAsync(
        PostgresTransactionScope scope,
        Guid characterId,
        string questId,
        string sourceActorDefinitionId,
        string definitionId,
        CancellationToken cancellationToken)
    {
        if (questDefinitions.Find(questId) is not { } quest)
        {
            return 0;
        }

        var log = await questLog.ReadAsync(scope, characterId, cancellationToken);
        var active = log.ActiveQuests.FirstOrDefault(value =>
            string.Equals(value.QuestId, quest.Id, StringComparison.Ordinal));
        var objective = active is null
            ? null
            : QuestProgression.TryCurrentStep(quest, active)?.Objectives.FirstOrDefault(value =>
                string.Equals(value.ItemId, definitionId, StringComparison.Ordinal)
                && QuestRules.FindDropSource(value, sourceActorDefinitionId) is not null);

        // The loaded catalog holds counts of at least 1, so 0 means no such objective.
        return objective?.Count ?? 0;
    }
}
