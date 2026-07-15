namespace AuthService.Items;

public sealed record ItemCatalogResponse(
    string CatalogId,
    string Revision,
    int FormatVersion,
    string BaseSecureContainerTierId,
    IReadOnlyList<ItemCatalogIdentityResponse> Categories,
    IReadOnlyList<ItemCatalogIdentityResponse> Tags,
    IReadOnlyList<ItemCatalogEquipmentSlotResponse> EquipmentSlots,
    IReadOnlyList<ItemCatalogDefinitionResponse> Definitions,
    IReadOnlyList<ItemCatalogSecureContainerTierResponse> SecureContainerTiers);

public sealed record ItemCatalogIdentityResponse(
    string Id,
    string DisplayName);

public sealed record ItemCatalogEquipmentSlotResponse(
    string Id,
    string DisplayName,
    int SortOrder);

public sealed record ItemCatalogDefinitionResponse(
    string Id,
    string DisplayName,
    string CategoryId,
    long UnitWeight,
    int MaximumStackSize,
    bool PlayerDestroyable,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> EquipmentSlots,
    bool SecureContainerEligible,
    IReadOnlyList<string> DefaultPolicies,
    ItemCatalogBagResponse? Bag);

public sealed record ItemCatalogBagResponse(
    long CarryCapacityBonus,
    IReadOnlyList<ItemCatalogBagSlotResponse> Slots);

public sealed record ItemCatalogBagSlotResponse(
    int SlotIndex,
    string SlotKind,
    IReadOnlyList<string> AcceptedTags);

public sealed record ItemCatalogSecureContainerTierResponse(
    string Id,
    string DisplayName,
    int SlotCapacity);
