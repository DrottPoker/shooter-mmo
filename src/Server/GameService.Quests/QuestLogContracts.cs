using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Quests;

// A character's quest log for its owning account. The client reads it once when it joins a
// Shard and resolves every text and rule from its bundled quest catalog of the same revision.
internal sealed record CharacterQuestLogResponse(
    Guid CharacterId,
    string QuestCatalogRevision,
    long QuestLogRevision,
    IReadOnlyList<ActiveQuestResponse> ActiveQuests,
    IReadOnlyList<CompletedQuestResponse> CompletedQuests);

internal sealed record ActiveQuestResponse(
    string QuestId,
    string StepId,
    DateTime AcceptedAt,
    IReadOnlyList<QuestObjectiveProgressResponse> Objectives);

internal sealed record QuestObjectiveProgressResponse(string ObjectiveId, int Progress);

internal sealed record CompletedQuestResponse(string QuestId, int CompletionCount, DateTime LastCompletedAt);

internal static class QuestLogResponses
{
    public static CharacterQuestLogResponse From(
        Guid characterId,
        string questCatalogRevision,
        CharacterQuestLogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new CharacterQuestLogResponse(
            characterId,
            questCatalogRevision,
            state.Revision,
            state.ActiveQuests
                .Select(quest => new ActiveQuestResponse(
                    quest.QuestId,
                    quest.StepId,
                    quest.AcceptedAt,
                    quest.Progress
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => new QuestObjectiveProgressResponse(entry.Key, entry.Value))
                        .ToArray()))
                .ToArray(),
            state.CompletedQuests
                .Select(quest => new CompletedQuestResponse(
                    quest.QuestId,
                    quest.CompletionCount,
                    quest.LastCompletedAt))
                .ToArray());
    }
}
