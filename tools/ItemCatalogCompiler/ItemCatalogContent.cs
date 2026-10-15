using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.ItemCatalogCompiler;

// content/items/catalog.json: the catalog-wide vocabulary shared by every definition.
public sealed class ItemCatalogHeaderDocument
{
    public int FormatVersion;
    public string? CatalogId;
    public string? BaseSecureContainerTierId;
    public ItemCatalogIdentityEntry[]? Categories;
    public ItemCatalogIdentityEntry[]? Tags;
    public ItemCatalogIdentityEntry[]? EquipmentSlots;
    public SecureContainerTierAuthoringEntry[]? SecureContainerTiers;
}

// Assembles the authoring document from catalog.json and one file per item definition.
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
        return new ItemCatalogAuthoringDocument
        {
            FormatVersion = header.FormatVersion,
            CatalogId = header.CatalogId,
            BaseSecureContainerTierId = header.BaseSecureContainerTierId,
            Categories = header.Categories,
            Tags = header.Tags,
            EquipmentSlots = header.EquipmentSlots,
            Definitions = definitions,
            SecureContainerTiers = header.SecureContainerTiers
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
}
