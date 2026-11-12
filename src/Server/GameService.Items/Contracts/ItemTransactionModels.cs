using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Contracts;

// The state that a handler locks or loads through the item transaction context. The kernel
// fills them; only what commands above Items read is public.

public sealed record CharacterLockRequest(
    Guid CharacterId,
    long? ExpectedRevision);

public sealed class LockedCharacterState
{
    public Guid CharacterId { get; internal set; }

    internal Guid AccountId { get; set; }

    internal long Revision { get; set; }

    internal long CarriedWeight { get; set; }

    internal long BaseCarryCapacity { get; set; }

    internal long CarryCapacity { get; set; }

    internal Guid PermanentInventoryContainerId { get; set; }

    internal Guid BankContainerId { get; set; }

    internal Guid RecoveryStorageContainerId { get; set; }
}

public sealed class LockedItem
{
    internal Guid ItemInstanceId { get; set; }

    internal string DefinitionId { get; set; } = string.Empty;

    internal int Quantity { get; set; }

    internal long Revision { get; set; }

    internal Guid? ContainerId { get; set; }

    internal int? ContainerSlotIndex { get; set; }

    internal Guid? EquippedCharacterId { get; set; }

    internal string? EquipmentSlotId { get; set; }

    internal string? SourceContainerType { get; set; }

    internal string? SourceContainerLifecycle { get; set; }

    internal Guid? SourceBagItemInstanceId { get; set; }

    internal Guid? OwningCharacterId { get; set; }

    internal bool IsBag { get; set; }

    internal long? BagCarryCapacityBonus { get; set; }

    internal Guid? BagContentsContainerId { get; set; }

    internal string? BagContentsLifecycle { get; set; }

    internal long? BagContentsRevision { get; set; }
}

public sealed record ResolvedItemDefinition(
    ItemDefinition RuntimeDefinition,
    bool IsBag);
