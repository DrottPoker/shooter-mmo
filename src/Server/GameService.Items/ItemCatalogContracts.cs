namespace Mmorpg.GameService.Items;

internal sealed record ItemCatalogResponse(
    string CatalogId,
    string Revision,
    int FormatVersion,
    IReadOnlyList<ItemCatalogIdentityResponse> Categories,
    IReadOnlyList<ItemCatalogIdentityResponse> Tags,
    IReadOnlyList<ItemCatalogEquipmentSlotResponse> EquipmentSlots,
    IReadOnlyList<ItemCatalogDefinitionResponse> Definitions);

internal sealed record ItemCatalogIdentityResponse(
    string Id,
    string DisplayName);

internal sealed record ItemCatalogEquipmentSlotResponse(
    string Id,
    string DisplayName,
    int SortOrder,
    int MaximumQuantity);

internal sealed record ItemCatalogDefinitionResponse(
    string Id,
    string DisplayName,
    string CategoryId,
    long UnitWeight,
    int MaximumStackSize,
    bool PlayerDestroyable,
    int RequiredCharacterLevel,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> EquipmentSlots,
    IReadOnlyList<string> BlockedEquipmentSlots,
    IReadOnlyList<string> DefaultPolicies,
    ItemCatalogBagResponse? Bag);

internal sealed record ItemCatalogBagResponse(
    long CarryCapacityBonus,
    IReadOnlyList<ItemCatalogBagSlotResponse> Slots);

internal sealed record ItemCatalogBagSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags);
