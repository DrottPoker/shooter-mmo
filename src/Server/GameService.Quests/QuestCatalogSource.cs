namespace Mmorpg.GameService.Quests;

// The compiled quest catalog that the build copies next to GameService, unless
// Quests:CatalogPath names another file.
public sealed record QuestCatalogSource(string RuntimeCatalogPath)
{
    private const string DefaultRelativePath = "WorldData/Quests/core.quest-catalog.json";

    public static QuestCatalogSource FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredPath = configuration["Quests:CatalogPath"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRelativePath
            : configuredPath;
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        return new QuestCatalogSource(Path.GetFullPath(resolvedPath));
    }
}
