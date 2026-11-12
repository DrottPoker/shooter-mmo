using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Reports the quests that a transaction changed, as the quest log holds them after it, so the
// worker can patch the client's quest log without reading it again.
internal static class QuestLogChanges
{
    public static async Task ReportAsync(
        ItemTransactionContext context,
        ICharacterQuestLog questLog,
        Guid characterId,
        IReadOnlyCollection<string> questIds,
        CancellationToken cancellationToken)
    {
        var state = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        QuestLogSection.Set(context.Journal, Of(characterId, state, questIds));
    }

    public static ItemTransactionQuestLogChange Of(
        Guid characterId,
        CharacterQuestLogState state,
        IReadOnlyCollection<string> questIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questIds);
        return new ItemTransactionQuestLogChange(
            characterId,
            state.Revision,
            questIds
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(questId =>
                {
                    var active = state.ActiveQuests.FirstOrDefault(quest =>
                        string.Equals(quest.QuestId, questId, StringComparison.Ordinal));
                    var completions = state.CompletedQuests.FirstOrDefault(quest =>
                        string.Equals(quest.QuestId, questId, StringComparison.Ordinal))?.CompletionCount ?? 0;
                    return new ItemTransactionQuestChange(
                        questId,
                        active?.StepId,
                        active is null
                            ? []
                            : active.Progress
                                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                                .Select(entry => new ItemTransactionQuestObjectiveProgress(entry.Key, entry.Value))
                                .ToArray(),
                        completions);
                })
                .ToArray());
    }
}
