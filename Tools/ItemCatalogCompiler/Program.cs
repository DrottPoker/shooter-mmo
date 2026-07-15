using ShooterMmo.Tools.ItemCatalogCompiler;
using ShooterMmo.WorldData.Items;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine(
        "Usage: ItemCatalogCompiler <authoring.json> <runtime.json> [--verify]");
    return 1;
}

var authoringPath = Path.GetFullPath(args[0]);
var runtimePath = Path.GetFullPath(args[1]);
var verifyOnly = args.Length == 3
    && string.Equals(args[2], "--verify", StringComparison.OrdinalIgnoreCase);
if (args.Length == 3 && !verifyOnly)
{
    Console.Error.WriteLine("The only supported third argument is --verify.");
    return 1;
}

try
{
    if (!File.Exists(authoringPath))
    {
        throw new FileNotFoundException("Item catalog authoring file does not exist.", authoringPath);
    }

    var authoringJson = await File.ReadAllTextAsync(authoringPath);
    var authoring = ItemCatalogJson.DeserializeAuthoring(authoringJson);
    var catalog = ItemCatalogCompiler.Compile(authoring);
    var expectedRuntimeJson = ItemCatalogJson.SerializeRuntime(catalog);
    var tierLabel = catalog.SecureContainerTiers.Length == 1 ? "tier" : "tiers";

    if (verifyOnly)
    {
        if (!File.Exists(runtimePath))
        {
            throw new InvalidDataException("Compiled item catalog runtime file is stale.");
        }

        var actualRuntimeJson = await File.ReadAllTextAsync(runtimePath);
        _ = ItemCatalogJson.DeserializeRuntime(actualRuntimeJson);
        if (!string.Equals(
            NormalizeLineEndings(actualRuntimeJson),
            expectedRuntimeJson,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Compiled item catalog runtime file is stale.");
        }

        Console.WriteLine(
            "Item catalog verified for catalog {0} at revision {1} with {2} definitions and {3} Secure Container {4}.",
            catalog.CatalogId,
            catalog.Revision,
            catalog.Definitions.Length,
            catalog.SecureContainerTiers.Length,
            tierLabel);
        return 0;
    }

    var runtimeDirectory = Path.GetDirectoryName(runtimePath);
    if (!string.IsNullOrWhiteSpace(runtimeDirectory))
    {
        Directory.CreateDirectory(runtimeDirectory);
    }

    await File.WriteAllTextAsync(runtimePath, expectedRuntimeJson);
    Console.WriteLine(
        "Item catalog compiled for catalog {0} at revision {1} with {2} definitions and {3} Secure Container {4}.",
        catalog.CatalogId,
        catalog.Revision,
        catalog.Definitions.Length,
        catalog.SecureContainerTiers.Length,
        tierLabel);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Item catalog compilation failed: " + exception.Message);
    return 1;
}

static string NormalizeLineEndings(string value)
{
    return value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
}
