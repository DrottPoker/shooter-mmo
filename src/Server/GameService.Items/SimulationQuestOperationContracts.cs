namespace Mmorpg.GameService.Items;

// A quest action of a live character. QuestNpcActorId is the actor definition id of the NPC
// whose quest capability the worker validated for this operation; abandoning and kill credits
// need none. A kill credit names the credited Mob's actor definition and tags instead of a
// quest. The route answers with a SimulationItemOperationResponse, whose QuestLog names the
// quests the action changed.
internal sealed record SimulationQuestOperationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string? WorkerId,
    string? WorkerRuntimeId,
    string? ShardId,
    string? SessionToken,
    string? OperationKind,
    string? QuestId,
    string? QuestNpcActorId = null,
    string? ObjectiveId = null,
    string? RewardChoiceDefinitionId = null,
    string? MobActorId = null,
    IReadOnlyList<string>? MobTags = null);
