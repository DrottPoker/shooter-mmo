using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions;

internal sealed record CharacterLockRequest(
    Guid CharacterId,
    long? ExpectedRevision);

internal sealed class LockedCharacterState
{
    public Guid CharacterId { get; set; }

    public Guid AccountId { get; set; }

    public long Revision { get; set; }

    public long CarriedWeight { get; set; }

    public long BaseCarryCapacity { get; set; }

    public long CarryCapacity { get; set; }

    public Guid PermanentInventoryContainerId { get; set; }

    public Guid BankContainerId { get; set; }

    public Guid RecoveryStorageContainerId { get; set; }
}

internal sealed class LockedContainer
{
    public Guid ContainerId { get; set; }

    public string ContainerType { get; set; } = string.Empty;

    public Guid? DirectOwnerCharacterId { get; set; }

    public Guid? BoundBagItemInstanceId { get; set; }

    public Guid? OwningCharacterId { get; set; }

    public int? SlotCapacity { get; set; }

    public long Revision { get; set; }

    public string Lifecycle { get; set; } = string.Empty;
}

internal sealed class LockedItem
{
    public Guid ItemInstanceId { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public long Revision { get; set; }

    public Guid? ContainerId { get; set; }

    public int? ContainerSlotIndex { get; set; }

    public Guid? EquippedCharacterId { get; set; }

    public string? EquipmentSlotId { get; set; }

    public string? SourceContainerType { get; set; }

    public string? SourceContainerLifecycle { get; set; }

    public Guid? SourceBagItemInstanceId { get; set; }

    public Guid? OwningCharacterId { get; set; }

    public bool IsBag { get; set; }

    public long? BagCarryCapacityBonus { get; set; }

    public Guid? BagContentsContainerId { get; set; }

    public string? BagContentsLifecycle { get; set; }

    public long? BagContentsRevision { get; set; }
}

internal sealed class LockedItemPolicy
{
    public Guid PolicyId { get; set; }

    public string PolicyKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public long Revision { get; set; }
}

internal sealed record ResolvedItemDefinition(
    ItemDefinition RuntimeDefinition,
    bool IsBag);

// An empty carried slot that accepts a new item, for kernel placement.
internal sealed record CarriedSlot(
    Guid ContainerId,
    int SlotIndex,
    string SlotKind);
