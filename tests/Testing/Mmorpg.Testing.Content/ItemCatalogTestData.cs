using Mmorpg.Testing;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Testing.Content;

// The real game content for content tests, and the test-owned fixture catalog for rule
// tests that must not change when content or balance changes.
public static class ItemCatalogTestData
{
    public static string ItemsDirectory => ContentLayout.ItemsDirectory(RepositoryPaths.Content);

    public static string FixtureItemsDirectory =>
        ContentLayout.ItemsDirectory(RepositoryPaths.ItemCatalogFixtureContent);

    public static ItemCatalogAuthoringDocument LoadAuthoring()
    {
        return ItemCatalogContent.Load(ItemsDirectory);
    }

    public static ItemCatalogRuntimeDocument Compile()
    {
        return ItemCatalogCompiler.Compile(LoadAuthoring());
    }

    public static ItemCatalogAuthoringDocument LoadFixtureAuthoring()
    {
        return ItemCatalogContent.Load(FixtureItemsDirectory);
    }

    public static ItemCatalogRuntimeDocument CompileFixture()
    {
        return ItemCatalogCompiler.Compile(LoadFixtureAuthoring());
    }

    public static string CatalogJson => File.ReadAllText(ContentLayout.CatalogPath(ItemsDirectory));

    public static string DefinitionJson(string definitionId)
    {
        return File.ReadAllText(ContentLayout.DefinitionPath(ItemsDirectory, definitionId));
    }

    public static string RuntimeJson => File.ReadAllText(
        ContentLayout.GeneratedItemCatalogPath(RepositoryPaths.Generated));

    public static string FixtureRuntimeJson => File.ReadAllText(
        RepositoryPaths.ItemCatalogFixtureRuntimePath);
}
