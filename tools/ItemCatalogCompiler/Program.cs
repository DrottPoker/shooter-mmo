using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.ItemCatalogCompiler;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: ItemCatalogCompiler " + ContentToolOptions.Usage);
    return 1;
}

try
{
    var authoring = ItemCatalogContent.Load(ContentLayout.ItemsDirectory(options!.ContentRoot));
    var catalog = ItemCatalogCompiler.Compile(authoring);
    GeneratedOutput.WriteOrVerifyText(
        ContentLayout.GeneratedItemCatalogPath(options.GeneratedRoot),
        ItemCatalogJson.SerializeRuntime(catalog),
        options.VerifyOnly,
        "Compiled item catalog");
    Console.WriteLine(
        "Item catalog {0} for catalog {1} at revision {2} with {3} definitions and {4} Secure Container {5}.",
        options.VerifyOnly ? "verified" : "compiled",
        catalog.CatalogId,
        catalog.Revision,
        catalog.Definitions.Length,
        catalog.SecureContainerTiers.Length,
        catalog.SecureContainerTiers.Length == 1 ? "tier" : "tiers");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Item catalog compilation failed: " + exception.Message);
    return 1;
}
