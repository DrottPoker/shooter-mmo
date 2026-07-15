namespace AuthService.Items;

public sealed record ItemCatalogSource(string RuntimeCatalogPath)
{
    private const string DefaultRelativePath = "WorldData/Items/core.item-catalog.json";

    public static ItemCatalogSource FromConfiguration(IConfiguration configuration)
    {
        var configuredPath = configuration["Items:CatalogPath"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRelativePath
            : configuredPath;
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        return new ItemCatalogSource(Path.GetFullPath(resolvedPath));
    }
}
