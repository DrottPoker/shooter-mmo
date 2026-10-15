using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.ItemCatalogCompiler;

public static class ItemCatalogJson
{
    public static ItemCatalogHeaderDocument DeserializeCatalog(string json)
    {
        return StrictContentJson.Deserialize<ItemCatalogHeaderDocument>(json, "Item catalog");
    }

    public static ItemDefinitionAuthoringEntry DeserializeDefinition(string json)
    {
        return StrictContentJson.Deserialize<ItemDefinitionAuthoringEntry>(
            json,
            "Item definition");
    }

    public static ItemCatalogRuntimeDocument DeserializeRuntime(string json)
    {
        return StrictContentJson.Deserialize<ItemCatalogRuntimeDocument>(
            json,
            "Item catalog runtime");
    }

    public static string SerializeRuntime(ItemCatalogRuntimeDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return StrictContentJson.Serialize(catalog);
    }
}
