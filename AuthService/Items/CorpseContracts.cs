namespace AuthService.Items;

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
    string PresentationKey);

public sealed record PlayerDeathPartitionResponse(
    Guid OperationId,
    Guid DeathEventId,
    DurableCorpseResponse Corpse,
    ItemTransactionCharacterRevision CharacterRevision,
    IReadOnlyList<Guid> RecoveryDeliveryIds);

public sealed record MobCorpseLootEntryRequest(
    Guid GrantId,
    string DefinitionId,
    int Quantity);

public sealed record CreatePersistentMobCorpseRequest(
    Guid OperationId,
    Guid CorpseId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SourceActorDefinitionId,
    string SourceDisplayName,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    double LifetimeSeconds,
    IReadOnlyList<MobCorpseLootEntryRequest> Loot);

public sealed record PersistentMobCorpseResponse(
    Guid OperationId,
    DurableCorpseResponse Corpse);

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
    int DestinationSlotIndex);

public sealed record CorpseRestoreResponse(
    DateTime DatabaseTime,
    IReadOnlyList<DurableCorpseResponse> Corpses);

public sealed record DurableCorpseResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string SourceDisplayName,
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
    IReadOnlyList<CorpseSectionResponse> Sections);

public sealed record CorpseSectionResponse(
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
    long? ExpectedPlayerBagContentsRevision = null);

public sealed record CorpseMutationResponse(
    ItemTransactionResult Transaction,
    CorpseViewSnapshotResponse? Corpse);

public sealed record CorpseViewSnapshotResponse(
    Guid CorpseId,
    Guid? SourceCharacterId,
    string SourceDisplayName,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    string PresentationKey,
    long Revision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<CorpseViewSectionResponse> Sections,
    IReadOnlyList<CorpsePresentationSnapshotResponse> PresentationSnapshots);

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

public static class CorpseInteractionOperationKinds
{
    public const string LootItem = "loot_item";
    public const string LootPartialStack = "loot_partial_stack";
    public const string DepositItem = "deposit_item";
    public const string DepositPartialStack = "deposit_partial_stack";
    public const string MoveItem = "move_item";
    public const string MovePartialStack = "move_partial_stack";
    public const string SwapBag = "swap_bag";
}
