namespace Mmorpg.GameService.Items;

// A quest action of a live character. QuestNpcActorId is the actor definition id of the NPC
// whose quest capability the worker validated for this operation; abandoning needs none.
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
    string? QuestNpcActorId = null);

// A committed quest action with the acting character's item state afterwards and the quests
// it changed, so the worker can patch the client's inventory and quest log.
internal sealed record SimulationQuestOperationResponse(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionItemChange>? ItemChanges,
    SimulationCharacterStateResponse? CharacterState,
    ItemTransactionQuestLogChange? QuestLog);
