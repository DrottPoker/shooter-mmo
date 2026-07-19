using ShooterMmo.Tools.ItemCatalogCompiler;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Unit;

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
        FindRepositoryRoot(),
        "WorldData",
        "Shared",
        "Authoring",
        "Items",
        "core.item-catalog.json");

    private static string RuntimePath => Path.Combine(
        FindRepositoryRoot(),
        "WorldData",
        "Shared",
        "Runtime",
        "Items",
        "core.item-catalog.json");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShooterMmo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Shooter MMO repository root.");
    }
}
