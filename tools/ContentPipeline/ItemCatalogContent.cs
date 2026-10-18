using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.ContentPipeline;

// content/items/catalog.json: the catalog-wide vocabulary shared by every definition.
public sealed class ItemCatalogHeaderDocument
{
    public int FormatVersion;
    public string? CatalogId;
    public string? BaseSecureContainerTierId;
    public ItemCharacterSettingsEntry? Character;
    public ItemEconomySettingsEntry? Economy;
    public ItemCatalogIdentityEntry[]? Categories;
    public ItemCatalogIdentityEntry[]? Tags;
    public ItemCatalogIdentityEntry[]? ToolKinds;
    public ItemEquipmentSlotEntry[]? EquipmentSlots;
    public SecureContainerTierAuthoringEntry[]? SecureContainerTiers;
    public ItemCatalogRetiredIdsEntry? Retired;
}

// Assembles the authoring document from catalog.json, one file per item definition, and
// one file per vendor profile. The item catalog compiler and the world actor compiler,
// which validates actor references to items and vendors, load item content the same way.
public static class ItemCatalogContent
{
    public static ItemCatalogAuthoringDocument Load(string itemsDirectory)
    {
        var header = StrictContentJson.DeserializeFile<ItemCatalogHeaderDocument>(
            ContentLayout.CatalogPath(itemsDirectory),
            "Item catalog");
        var definitions = ContentLayout.ListDefinitionFiles(itemsDirectory)
            .Select(LoadDefinition)
            .ToArray();
        var vendors = ContentLayout.ListVendorFiles(itemsDirectory)
            .Select(LoadVendor)
            .ToArray();
        return new ItemCatalogAuthoringDocument
        {
            FormatVersion = header.FormatVersion,
            CatalogId = header.CatalogId,
            BaseSecureContainerTierId = header.BaseSecureContainerTierId,
            Character = header.Character,
            Economy = header.Economy,
            Categories = header.Categories,
            Tags = header.Tags,
            ToolKinds = header.ToolKinds,
            EquipmentSlots = header.EquipmentSlots,
            Definitions = definitions,
            SecureContainerTiers = header.SecureContainerTiers,
            Vendors = vendors,
            Retired = header.Retired
        };
    }

    private static ItemDefinitionAuthoringEntry LoadDefinition(ContentDefinitionFile file)
    {
        var definition = StrictContentJson.DeserializeFile<ItemDefinitionAuthoringEntry>(
            file.Path,
            "Item definition");
        if (!string.Equals(definition.Id, file.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Item definition file '{file.Path}' must contain id '{file.Id}'.");
        }

        return definition;
    }

    private static VendorProfileAuthoringEntry LoadVendor(ContentDefinitionFile file)
    {
        var vendor = StrictContentJson.DeserializeFile<VendorProfileAuthoringEntry>(
            file.Path,
            "Vendor profile");
        if (!string.Equals(vendor.Id, file.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Vendor profile file '{file.Path}' must contain id '{file.Id}'.");
        }

        return vendor;
    }
}
