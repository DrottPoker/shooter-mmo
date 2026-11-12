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
    public Guid ItemInstanceId { get; internal set; }

    public string DefinitionId { get; internal set; } = string.Empty;

    public int Quantity { get; internal set; }

    internal long Revision { get; set; }

    public Guid? ContainerId { get; internal set; }

    internal int? ContainerSlotIndex { get; set; }

    internal Guid? EquippedCharacterId { get; set; }

    public string? EquipmentSlotId { get; internal set; }

    public string? SourceContainerType { get; internal set; }

    public string? SourceContainerLifecycle { get; internal set; }

    internal Guid? SourceBagItemInstanceId { get; set; }

    internal Guid? OwningCharacterId { get; set; }

    internal bool IsBag { get; set; }

    internal long? BagCarryCapacityBonus { get; set; }

    internal Guid? BagContentsContainerId { get; set; }

    internal string? BagContentsLifecycle { get; set; }

    internal long? BagContentsRevision { get; set; }
}

// An item policy row; ItemPolicyStates turns policies into rule inputs.
public sealed class LockedItemPolicy
{
    internal Guid PolicyId { get; set; }

    internal string PolicyKind { get; set; } = string.Empty;

    internal string Status { get; set; } = string.Empty;

    internal string SourceKind { get; set; } = string.Empty;

    internal string SourceId { get; set; } = string.Empty;

    internal long Revision { get; set; }
}

public sealed record ResolvedItemDefinition(
    ItemDefinition RuntimeDefinition,
    bool IsBag);
