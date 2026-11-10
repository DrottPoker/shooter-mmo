using System.Net;
using System.Text.Json.Serialization;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed record ConsumeSimulationJoinTicketRequest(
    string Ticket,
    string WorkerId,
    string RuntimeId,
    string ShardId);

public sealed record ConsumedSimulationJoinTicketResponse(
    Guid AccountId,
    Guid CharacterId,
    string CharacterName,
    string ShardId,
    string WorldId,
    string WorkerId,
    string WorkerRuntimeId,
    Guid SimulationSessionId,
    string SimulationSessionToken,
    DateTime SessionExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool IsReconnect)
{
    public bool IsSyntheticBot { get; init; }

    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long SessionLeaseDurationMilliseconds { get; init; }

    // What the character has equipped at ItemStateRevision.
    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];

    // How the character looks; null for a development bot, which the worker dresses itself.
    public SimulationAppearance? Appearance { get; init; }
}

// A character's body model, the choices of its look, and whether it lowered its headwear.
public sealed record SimulationAppearance(
    string BodyModel,
    IReadOnlyList<SimulationAppearanceChoice> Choices,
    bool HeadwearLowered);

public sealed record SimulationAppearanceChoice(string Kind, string Slot, string Option);

// A change of the look of a session's character: "headwear" with HeadwearLowered, or in
// Development "development_look" with a body model and choices.
public sealed record SimulationAppearanceChangeRequest(
    string WorkerRuntimeId,
    string SessionToken,
    string Change,
    bool? HeadwearLowered,
    string? BodyModel,
    IReadOnlyList<SimulationAppearanceChoice>? Choices);

// One occupied equipment slot, such as the equipped weapon or the arrow stack.
public sealed record SimulationEquippedItem(
    string EquipmentSlotId,
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision);

// The acting character's carry state and equipment after an operation, at one revision.
public sealed record SimulationCharacterState(
    Guid CharacterId,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    IReadOnlyList<SimulationEquippedItem> Equipment);

public sealed record SimulationSessionCredentialRequest(
    string WorkerRuntimeId,
    string SessionToken);

public sealed record SimulationSessionLeaseResponse(
    Guid SimulationSessionId,
    Guid CharacterId,
    string ShardId,
    string WorkerId,
    string WorkerRuntimeId,
    DateTime ExpiresAt,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    bool Released)
{
    // The lease duration lets the worker set a deadline on its own clock, so clock
    // differences between the worker and the database never move the deadline.
    public long LeaseDurationMilliseconds { get; init; }

    // What the character has equipped at ItemStateRevision.
    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];
}

// VendorProfileId is the vendor profile of the validated vendor capability, or null.
public sealed record SimulationItemAccessRequest(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    string? VendorProfileId = null);

public sealed record SimulationItemRevisionExpectation(
    Guid ItemInstanceId,
    long Revision);

public sealed record SimulationItemOperationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    SimulationItemAccessRequest Access,
    string OperationKind,
    long ExpectedCharacterRevision,
    Guid? ItemInstanceId = null,
    long? ExpectedItemRevision = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null,
    Guid? DestinationContainerId = null,
    int? DestinationSlotIndex = null,
    int? Quantity = null,
    string? EquipmentSlotId = null,
    Guid? RecoveryDeliveryId = null,
    long? ExpectedRecoveryDeliveryRevision = null,
    IReadOnlyList<SimulationItemRevisionExpectation>? Items = null,
    Guid? InteractionSessionId = null,
    string? CapabilityId = null,
    string? DefinitionId = null,
    long? ExpectedPrice = null,
    string? ResourceProfileId = null,
    string? ResourceNodeId = null,
    string? RequiredToolKind = null,
    string? ResourceActorId = null);

public sealed record SimulationPlayerDeathRequest(
    Guid OperationId,
    Guid DeathEventId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    long ExpectedCharacterRevision,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    SimulationDeathAmmunitionRequest? AmmunitionConsumption = null,
    SimulationDeathKillerRequest? Killer = null);

// The shots the worker had not committed yet when the character died.
public sealed record SimulationDeathAmmunitionRequest(
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

// The killer kind is mob or character, and the id is its actor definition or character id.
public sealed record SimulationDeathKillerRequest(string Kind, string Id);

public sealed record PlayerDeathPartitionResponse(
    Guid OperationId,
    Guid DeathEventId,
    DurableCorpseResponse Corpse,
    SimulationItemCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    SimulationCharacterState? CharacterState = null);

// QuestId names the quest whose lineage a quest drop gets; ordinary loot names none.
public sealed record MobCorpseLootEntryRequest(
    Guid GrantId,
    string DefinitionId,
    int Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuestId = null);

public sealed record CreatePersistentMobCorpseRequest(
    Guid OperationId,
    Guid CorpseId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SourceActorDefinitionId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    double LifetimeSeconds,
    IReadOnlyList<MobCorpseLootEntryRequest> Loot)
{
    // The player the Mob credited, who alone may loot the corpse, or null when everyone may.
    public Guid? OwnerCharacterId { get; init; }

    // The corpses of one Mob death share it, so a viewer sees one body.
    public Guid? LootGroupId { get; init; }
}

// QuestDrops are the quest drops that the corpse keeps for its owner, in corpse order.
public sealed record PersistentMobCorpseResponse(
    Guid OperationId,
    DurableCorpseResponse Corpse)
{
    public IReadOnlyList<SimulationQuestDrop>? QuestDrops { get; init; }
}

public sealed record SimulationMobLootGrantRequest(
    Guid GrantId,
    Guid AccountId,
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    string DefinitionId,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    string? EquipmentSlotId = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null,
    string? QuestId = null);

// The quest drops that a dying Mob rolled for the owner of a live corpse.
public sealed record SimulationQuestDropRequest(
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    string QuestCatalogRevision,
    string SourceActorDefinitionId,
    IReadOnlyList<SimulationQuestDrop> Drops);

public sealed record SimulationQuestDrop(string QuestId, string DefinitionId, int Quantity);

// The drops that the owner's quests still need, each at most what they need, in request order;
// a drop that no quest needs is left out.
public sealed record SimulationQuestDropResponse(IReadOnlyList<SimulationQuestDrop> Drops);

// Loot all on a live Mob corpse: every loot entry still in the corpse, in its slot order.
public sealed record SimulationMobLootAllRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    IReadOnlyList<MobCorpseLootEntryRequest> Loot);

// The committed transaction and the grants that left the corpse, including any that an earlier
// operation had already granted.
public sealed record SimulationMobLootAllResponse(
    SimulationItemTransactionResponse Transaction,
    IReadOnlyList<Guid> GrantedGrantIds);

public sealed record SimulationItemTransactionResponse(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    SimulationItemTransactionError? Error,
    IReadOnlyList<SimulationItemCharacterRevision> CharacterRevisions,
    IReadOnlyList<SimulationItemContainerRevision> ContainerRevisions,
    IReadOnlyList<SimulationItemInstanceRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    SimulationCharacterState? CharacterState = null,
    IReadOnlyList<SimulationItemChange>? ItemChanges = null,
    SimulationQuestLogChange? QuestLog = null);

// The quests a committed transaction changed, as the quest log holds them after it, with the
// quest log revision after it. A quest without a step is no longer active.
public sealed record SimulationQuestLogChange(
    Guid CharacterId,
    long Revision,
    IReadOnlyList<SimulationQuestChange> Quests);

public sealed record SimulationQuestChange(
    string QuestId,
    string? StepId,
    IReadOnlyList<SimulationQuestObjectiveProgress> Objectives,
    int CompletionCount);

public sealed record SimulationQuestObjectiveProgress(string ObjectiveId, int Progress);

// A quest action of a live character on GameService's quest route, which answers like the
// item route. QuestNpcActorId is the actor definition of the NPC whose quests capability the
// worker validated; abandoning needs none, and a kill credit names the Mob instead. Every quest
// request names the revision of the quest catalog the worker runs, which GameService refuses
// with simulation_worker_quest_catalog_mismatch when it runs another.
public sealed record SimulationQuestOperationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    string QuestCatalogRevision,
    string OperationKind,
    string? QuestId,
    string? QuestNpcActorId = null,
    string? ObjectiveId = null,
    string? RewardChoiceDefinitionId = null,
    string? MobActorId = null,
    IReadOnlyList<string>? MobTags = null);

public sealed record SimulationQuestLogRequest(
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    string QuestCatalogRevision);

// A joining character's quest log with the revision of the quest catalog GameService runs.
public sealed record SimulationQuestLogResponse(
    Guid CharacterId,
    string QuestCatalogRevision,
    long QuestLogRevision,
    IReadOnlyList<SimulationActiveQuest> ActiveQuests,
    IReadOnlyList<SimulationCompletedQuest> CompletedQuests);

public sealed record SimulationActiveQuest(
    string QuestId,
    string StepId,
    DateTime AcceptedAt,
    IReadOnlyList<SimulationQuestObjectiveProgress> Objectives);

public sealed record SimulationCompletedQuest(
    string QuestId,
    int CompletionCount,
    DateTime LastCompletedAt);

// The final state of one item a transaction changed. GameService leaves the list out when
// the transaction changed too many items, and a removed item has no definition. A Bag in
// the Bag slot names its contents container.
public sealed record SimulationItemChange(
    Guid ItemInstanceId,
    bool Removed,
    string? DefinitionId,
    int Quantity,
    long Revision,
    Guid? ContainerId,
    int? ContainerSlotIndex,
    Guid? EquippedCharacterId,
    string? EquipmentSlotId,
    IReadOnlyList<SimulationItemPolicySummary>? Policies,
    Guid? BagContentsContainerId = null,
    long? BagContentsRevision = null);

public sealed record SimulationItemPolicySummary(
    string PolicyKind,
    string Status,
    string ProtectionSource);

public sealed record SimulationItemTransactionError(string Code, string Message);

// One batch of shots from the equipped ammunition stack. The operation id is the batch
// id, so a retry replays the stored result instead of consuming twice.
public sealed record SimulationAmmunitionConsumptionRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    long ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

// Currency is the balance after the commit, or null in a result stored before it existed.
public sealed record SimulationItemCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity,
    long? Currency = null);

public sealed record SimulationItemContainerRevision(Guid ContainerId, long Revision);

public sealed record SimulationItemInstanceRevision(Guid ItemInstanceId, long Revision);

public sealed record SimulationWorkerHeartbeatResponse(
    string WorkerId,
    string RuntimeId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string RuleSet,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    string ItemCatalogRevision,
    string QuestCatalogRevision,
    DateTime LastHeartbeatAt,
    DateTime OnlineUntil);

public sealed record SimulationWorkerHeartbeatRequest(
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string RuntimeId,
    DateTime StartedAt,
    string Host,
    int UdpPort,
    int MaxConnections,
    int ActiveConnections,
    int ProtocolVersion,
    string SimulationRevision,
    string CollisionRevision,
    string ItemCatalogRevision,
    string QuestCatalogRevision);

public sealed record SimulationWorkerOfflineRequest(string RuntimeId);

public sealed record SimulationWorkerOfflineResponse(
    string WorkerId,
    string RuntimeId,
    DateTime OfflineAt);

public sealed record CorpseRestoreResponse(
    DateTime DatabaseTime,
    IReadOnlyList<DurableCorpseResponse> Corpses);

public sealed record DurableCorpseResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string? SourceCharacterName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsEmpty,
    IReadOnlyList<CorpseSectionResponse> Sections)
{
    // The actor definition of the Mob that left the corpse; null for a character's corpse,
    // which has SourceCharacterName instead.
    public string? SourceActorDefinitionId { get; init; }

    // How a player corpse looks; null for a Mob corpse.
    public CorpseLookResponse? Look { get; init; }

    // The character that alone may loot a Mob's corpse, or null when everyone may.
    public Guid? OwnerCharacterId { get; init; }

    // The corpses of one Mob death share it; null for every other corpse.
    public Guid? LootGroupId { get; init; }
}

// The dead character's body model, look, and headwear, and the items still in the corpse's
// equipment section, which the corpse wears until someone loots them.
public sealed record CorpseLookResponse(
    string BodyModel,
    IReadOnlyList<SimulationAppearanceChoice> Choices,
    IReadOnlyList<CorpseLookEquipmentResponse> Equipment,
    bool HeadwearLowered);

public sealed record CorpseLookEquipmentResponse(string EquipmentSlotId, string DefinitionId);

public sealed record CorpseSectionResponse(
    string SectionKind,
    Guid ContainerId,
    long ContainerRevision,
    int ItemCount);

public sealed record SimulationCorpseOpenRequest(
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken);

public sealed record SimulationCorpseMutationRequest(
    Guid OperationId,
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken,
    string OperationKind,
    long ExpectedCorpseRevision,
    Guid? ItemInstanceId = null,
    long? ExpectedItemRevision = null,
    int? Quantity = null,
    Guid? DestinationContainerId = null,
    long? ExpectedDestinationContainerRevision = null,
    int? DestinationSlotIndex = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null,
    Guid? CorpseBagContentsContainerId = null,
    long? ExpectedCorpseBagContentsRevision = null,
    Guid? PlayerBagItemInstanceId = null,
    long? ExpectedPlayerBagRevision = null,
    Guid? PlayerBagContentsContainerId = null,
    long? ExpectedPlayerBagContentsRevision = null,
    string? EquipmentSlotId = null);

public sealed record CorpseMutationResponse(
    SimulationItemTransactionResponse Transaction,
    CorpseViewSnapshotResponse? Corpse,
    SimulationCharacterState? CharacterState = null);

public sealed record CorpseViewSnapshotResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string? SourceCharacterName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<CorpseViewSectionResponse> Sections,
    IReadOnlyList<CorpsePresentationSnapshotResponse> PresentationSnapshots)
{
    // The actor definition of the Mob that left the corpse; null for a character's corpse,
    // which has SourceCharacterName instead.
    public string? SourceActorDefinitionId { get; init; }

    // The character that alone may loot a Mob's corpse, or null when everyone may.
    public Guid? OwnerCharacterId { get; init; }

    // The corpses of one Mob death share it; null for every other corpse.
    public Guid? LootGroupId { get; init; }
}

public sealed record CorpseViewSectionResponse(
    string SectionKind,
    Guid ContainerId,
    string ContainerType,
    long ContainerRevision,
    int SlotCapacity,
    IReadOnlyList<CorpseViewSlotResponse> Slots);

public sealed record CorpseViewSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    CorpseViewItemResponse? Item,
    string EquipmentSlotId = "");

public sealed record CorpseViewItemResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    Guid? BagContentsContainerId,
    long? BagContentsRevision);

public sealed record CorpsePresentationSnapshotResponse(
    string SnapshotKind,
    int SortOrder,
    string? DefinitionId,
    string? EquipmentSlotId,
    string? PolicyKind,
    string PresentationPayload);

public sealed record GameServiceProblemDetails(string? Code, string? Detail, string? Message);

public sealed record GameServiceResult<T>(T? Value, SimulationWorkerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static GameServiceResult<T> Success(T value)
    {
        return new GameServiceResult<T>(value, null, (int)HttpStatusCode.OK);
    }

    public static GameServiceResult<T> Failure(int statusCode, string code, string message)
    {
        return new GameServiceResult<T>(
            default,
            new SimulationWorkerErrorResponse(code, message),
            statusCode);
    }
}

public sealed record SimulationWorkerErrorResponse(string Code, string Message);
