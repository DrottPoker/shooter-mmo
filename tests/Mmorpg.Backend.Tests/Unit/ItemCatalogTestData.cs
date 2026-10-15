using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Backend.Tests.Unit;

internal static class ItemCatalogTestData
{
    public static string ItemsDirectory => ContentLayout.ItemsDirectory(RepositoryPaths.Content);

    public static ItemCatalogAuthoringDocument LoadAuthoring()
    {
        return ItemCatalogContent.Load(ItemsDirectory);
    }

    public static ItemCatalogRuntimeDocument Compile()
    {
        return ItemCatalogCompiler.Compile(LoadAuthoring());
    }

    public static string CatalogJson => File.ReadAllText(ContentLayout.CatalogPath(ItemsDirectory));

    public static string DefinitionJson(string definitionId)
    {
        return File.ReadAllText(ContentLayout.DefinitionPath(ItemsDirectory, definitionId));
    }

    public static string RuntimeJson => File.ReadAllText(
        ContentLayout.GeneratedItemCatalogPath(RepositoryPaths.Generated));
}
