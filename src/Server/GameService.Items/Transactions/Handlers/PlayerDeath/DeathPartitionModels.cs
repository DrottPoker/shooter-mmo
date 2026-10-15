using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

internal sealed record DeathPartitionItem(
    DeathItemSourceRow Source,
    LockedItem Item,
    ResolvedItemDefinition Definition,
    ItemDeathDisposition Disposition,
    string? BeforeState)
{
    public bool IsEquippedBag => Definition.IsBag
        && string.Equals(Source.SourceKind, "equipment", StringComparison.Ordinal)
        && string.Equals(Source.EquipmentSlotId, "bag", StringComparison.Ordinal);
}

internal sealed record DeathRecoveryGroup(
    string SourceKind,
    bool ConsumeInsurance,
    IReadOnlyList<DeathPartitionItem> Items);

internal sealed record DeathRecoveryAssignment(
    DeathRecoveryGroup Group,
    DeathPartitionItem Item,
    Guid DeliveryId,
    int ItemOrder,
    int SlotIndex);

internal sealed class DeathItemSourceRow
{
    public Guid ItemInstanceId { get; set; }

    public Guid? ContainerId { get; set; }

    public int? ContainerSlotIndex { get; set; }

    public string? EquipmentSlotId { get; set; }

    public int? EquipmentSortOrder { get; set; }

    public Guid? BagContentsContainerId { get; set; }

    public string SourceKind { get; set; } = string.Empty;
}
