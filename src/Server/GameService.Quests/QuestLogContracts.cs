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
