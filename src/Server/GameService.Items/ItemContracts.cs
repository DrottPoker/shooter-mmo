namespace Mmorpg.GameService.Items;

internal sealed record CharacterInventorySnapshotResponse(
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
    int MovementMultiplierBasisPoints,
    long Currency);

internal sealed record ItemContainerSnapshotResponse(
    Guid ContainerId,
    string ContainerType,
    long Revision,
    int? SlotCapacity,
    IReadOnlyList<ItemSlotSnapshotResponse> Slots);

internal sealed record ItemSlotSnapshotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags,
    ItemInstanceSnapshotResponse? Item);

internal sealed record EquipmentSlotSnapshotResponse(
    string EquipmentSlotId,
    int SortOrder,
    ItemInstanceSnapshotResponse? Item);

internal sealed record ItemInstanceSnapshotResponse(
    Guid ItemInstanceId,
    string DefinitionId,
    int Quantity,
    long Revision,
    IReadOnlyList<ItemPolicySummaryResponse> Policies);

internal sealed record ItemPolicySummaryResponse(
    string PolicyKind,
    string Status,
    string ProtectionSource);

internal sealed record EquippedBagSnapshotResponse(
    ItemInstanceSnapshotResponse Item,
    ItemContainerSnapshotResponse Contents);

internal sealed record SecureContainerSnapshotResponse(
    string TierId,
    long EntitlementRevision,
    ItemContainerSnapshotResponse Contents);

internal sealed record CharacterBankSnapshotResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    ItemContainerSnapshotResponse Bank);

internal sealed record CharacterRecoverySnapshotResponse(
    Guid CharacterId,
    string CatalogRevision,
    long ItemStateRevision,
    RecoveryStorageSnapshotResponse RecoveryStorage);

internal sealed record RecoveryStorageSnapshotResponse(
    Guid ContainerId,
    long Revision,
    IReadOnlyList<RecoveryDeliverySnapshotResponse> Deliveries);

internal sealed record RecoveryDeliverySnapshotResponse(
    Guid DeliveryId,
    long Revision,
    string SourceKind,
    DateTime CreatedAt,
    DateTime? AvailableAt,
    DateTime? ExpiresAt,
    DateTime? ClaimedAt,
    IReadOnlyList<RecoveryDeliveryItemSnapshotResponse> Items);

internal sealed record RecoveryDeliveryItemSnapshotResponse(
    int ItemOrder,
    int ContainerSlotIndex,
    ItemInstanceSnapshotResponse Item);
