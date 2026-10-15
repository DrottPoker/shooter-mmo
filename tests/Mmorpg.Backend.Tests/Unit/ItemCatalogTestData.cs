using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Backend.Tests.Unit;

internal static class ItemCatalogTestData
{
    public static ItemCatalogAuthoringDocument LoadAuthoring()
    {
        return ItemCatalogJson.DeserializeAuthoring(File.ReadAllText(AuthoringPath));
    }

    public static ItemCatalogRuntimeDocument Compile()
    {
        return ItemCatalogCompiler.Compile(LoadAuthoring());
    }

    public static string AuthoringJson => File.ReadAllText(AuthoringPath);

    public static string RuntimeJson => File.ReadAllText(RuntimePath);

    private static string AuthoringPath => Path.Combine(
        RepositoryPaths.SharedContent,
        "Authoring",
        "Items",
        "core.item-catalog.json");

    private static string RuntimePath => Path.Combine(
        RepositoryPaths.SharedContent,
        "Runtime",
        "Items",
        "core.item-catalog.json");
}
