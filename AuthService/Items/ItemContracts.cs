namespace AuthService.Items;

public sealed record CharacterInventorySnapshotResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    ItemContainerSnapshotResponse PermanentInventory,
    IReadOnlyList<EquipmentSlotSnapshotResponse> Equipment,
    EquippedBagSnapshotResponse? EquippedBag,
    ItemContainerSnapshotResponse Bank,
    SecureContainerSnapshotResponse SecureContainer,
    RecoveryStorageSnapshotResponse RecoveryStorage,
    long CarriedWeight,
    long CarryCapacity,
    int LoadRatioBasisPoints,
    bool SprintEligible,
    int MovementMultiplierBasisPoints);

public sealed record ItemContainerSnapshotResponse(
    Guid ContainerId,
    string ContainerType,
    long Revision,
    int? SlotCapacity,
    IReadOnlyList<ItemSlotSnapshotResponse> Slots);

public sealed record ItemSlotSnapshotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    ItemInstanceSnapshotResponse? Item);

public sealed record EquipmentSlotSnapshotResponse(
    string EquipmentSlotId,
    int SortOrder,
    ItemInstanceSnapshotResponse? Item);

public sealed record ItemInstanceSnapshotResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    IReadOnlyList<ItemPolicySummaryResponse> Policies);

public sealed record ItemPolicySummaryResponse(
    string PolicyKind,
    string Status);

public sealed record EquippedBagSnapshotResponse(
    ItemInstanceSnapshotResponse Item,
    ItemContainerSnapshotResponse Contents);

public sealed record SecureContainerSnapshotResponse(
    string TierId,
    long EntitlementRevision,
    ItemContainerSnapshotResponse Contents);

public sealed record CharacterBankSnapshotResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    ItemContainerSnapshotResponse Bank);

public sealed record CharacterRecoverySnapshotResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    RecoveryStorageSnapshotResponse RecoveryStorage);

public sealed record RecoveryStorageSnapshotResponse(
    Guid ContainerId,
    long Revision,
    IReadOnlyList<RecoveryDeliverySnapshotResponse> Deliveries);

public sealed record RecoveryDeliverySnapshotResponse(
    Guid DeliveryId,
    string SourceKind,
    DateTime CreatedAt,
    DateTime? AvailableAt,
    DateTime? ExpiresAt,
    DateTime? ClaimedAt,
    IReadOnlyList<RecoveryDeliveryItemSnapshotResponse> Items);

public sealed record RecoveryDeliveryItemSnapshotResponse(
    int ItemOrder,
    int ContainerSlotIndex,
    ItemInstanceSnapshotResponse Item);
