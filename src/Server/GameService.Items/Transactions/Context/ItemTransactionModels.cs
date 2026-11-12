namespace Mmorpg.GameService.Items.Transactions;

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

// An empty carried slot that accepts a new item, for kernel placement.
internal sealed record CarriedSlot(
    Guid ContainerId,
    int SlotIndex,
    string SlotKind);

// One audit change of the journal, which the finalizer writes as an item_operation_changes row.
internal sealed record ItemAuditChange(
    string ChangeKind,
    Guid? ItemInstanceId,
    string? BeforeState,
    string? AfterState);
