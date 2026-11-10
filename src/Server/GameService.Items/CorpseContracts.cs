using System.Text.Json.Serialization;

namespace Mmorpg.GameService.Items;

internal sealed record SimulationPlayerDeathRequest(
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
internal sealed record SimulationDeathAmmunitionRequest(
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

// The killer kind is mob or character, and the id is its actor definition or character id.
internal sealed record SimulationDeathKillerRequest(string Kind, string Id);

internal sealed record PlayerDeathPartitionResponse(
    Guid OperationId,
    Guid DeathEventId,
    DurableCorpseResponse Corpse,
    ItemTransactionCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    SimulationCharacterStateResponse? CharacterState = null);

// QuestId names the quest whose lineage a quest drop gets; ordinary loot names none.
internal sealed record MobCorpseLootEntryRequest(
    Guid GrantId,
    string DefinitionId,
    int Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuestId = null);

internal sealed record CreatePersistentMobCorpseRequest(
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

// QuestDrops are the quest drops that the corpse keeps for its owner, in corpse order, so the
// worker can tell the owner.
internal sealed record PersistentMobCorpseResponse(
    Guid OperationId,
    DurableCorpseResponse Corpse)
{
    public IReadOnlyList<SimulationQuestDrop> QuestDrops { get; init; } = [];
}

internal sealed record SimulationMobLootGrantRequest(
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

// Loot all on a live Mob corpse: every loot entry still in the corpse, in its slot order.
internal sealed record SimulationMobLootAllRequest(
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
internal sealed record SimulationMobLootAllResponse(
    SimulationItemOperationResponse Transaction,
    IReadOnlyList<Guid> GrantedGrantIds);

internal sealed record CorpseRestoreResponse(
    DateTime DatabaseTime,
    IReadOnlyList<DurableCorpseResponse> Corpses);

internal sealed record DurableCorpseResponse(
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
    // The actor definition of the Mob that left the corpse, which the client names from its
    // text tables; null for a character's corpse, which has SourceCharacterName instead.
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
internal sealed record CorpseLookResponse(
    string BodyModel,
    IReadOnlyList<CorpseLookChoiceResponse> Choices,
    IReadOnlyList<CorpseLookEquipmentResponse> Equipment,
    bool HeadwearLowered);

internal sealed record CorpseLookChoiceResponse(string Kind, string Slot, string Option);

internal sealed record CorpseLookEquipmentResponse(string EquipmentSlotId, string DefinitionId);

internal sealed record CorpseSectionResponse(
    string SectionKind,
    Guid ContainerId,
    long ContainerRevision,
    int ItemCount);

internal sealed record CorpsePartitionRecord(
    Guid DeathEventId,
    Guid OriginalOperationId,
    Guid CharacterId,
    DurableCorpseResponse Corpse,
    ItemTransactionCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds);

internal sealed record SimulationCorpseOpenRequest(
    Guid AccountId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SessionToken);

internal sealed record SimulationCorpseMutationRequest(
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

internal sealed record CorpseMutationResponse(
    ItemTransactionResult Transaction,
    CorpseViewSnapshotResponse? Corpse,
    SimulationCharacterStateResponse? CharacterState = null);

internal sealed record CorpseViewSnapshotResponse(
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
    // The actor definition of the Mob that left the corpse, which the client names from its
    // text tables; null for a character's corpse, which has SourceCharacterName instead.
    public string? SourceActorDefinitionId { get; init; }

    // The character that alone may loot a Mob's corpse, or null when everyone may.
    public Guid? OwnerCharacterId { get; init; }

    // The corpses of one Mob death share it; null for every other corpse.
    public Guid? LootGroupId { get; init; }
}

internal sealed record CorpseViewSectionResponse(
    string SectionKind,
    Guid ContainerId,
    string ContainerType,
    long ContainerRevision,
    int SlotCapacity,
    IReadOnlyList<CorpseViewSlotResponse> Slots);

internal sealed record CorpseViewSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    CorpseViewItemResponse? Item,
    string EquipmentSlotId = "");

internal sealed record CorpseViewItemResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    Guid? BagContentsContainerId,
    long? BagContentsRevision);

internal sealed record CorpsePresentationSnapshotResponse(
    string SnapshotKind,
    int SortOrder,
    string? DefinitionId,
    string? EquipmentSlotId,
    string? PolicyKind,
    string PresentationPayload);

internal static class CorpseInteractionOperationKinds
{
    public const string LootItem = "loot_item";
    public const string LootPartialStack = "loot_partial_stack";
    public const string LootItemToEquipment = "loot_item_to_equipment";
    public const string DepositItem = "deposit_item";
    public const string DepositPartialStack = "deposit_partial_stack";
    public const string MoveItem = "move_item";
    public const string MovePartialStack = "move_partial_stack";
    public const string SwapBag = "swap_bag";
    public const string LootAll = "loot_all";
}
