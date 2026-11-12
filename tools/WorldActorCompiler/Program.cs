using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Text;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: WorldActorCompiler " + ContentToolOptions.Usage);
    return 1;
}

try
{
    var contentRoot = options!.ContentRoot;
    var catalogAuthoring = WorldActorContent.LoadCatalog(ContentLayout.ActorsDirectory(contentRoot));
    var itemCatalog = LoadItemCatalog(options.GeneratedRoot);
    var worldIds = WorldActorContent.ListWorldIds(contentRoot);
    if (worldIds.Length == 0)
    {
        throw new InvalidDataException(
            $"No World exists below '{ContentLayout.WorldsDirectory(contentRoot)}'.");
    }

    var catalog = CompileCatalog(catalogAuthoring, itemCatalog);
    GeneratedOutput.WriteOrVerifyText(
        ContentLayout.GeneratedActorCatalogPath(options.GeneratedRoot),
        WorldActorJson.SerializeCatalogRuntime(catalog),
        options.VerifyOnly,
        "Compiled world actor catalog");
    Console.WriteLine(
        "World actor catalog {0} for catalog {1} at revision {2} with {3} definitions.",
        options.VerifyOnly ? "verified" : "compiled",
        catalog.CatalogId,
        catalog.Revision,
        catalog.Actors.Length);

    foreach (var worldId in worldIds)
    {
        var spawns = CompileSpawns(catalog, contentRoot, worldId);
        GeneratedOutput.WriteOrVerifyText(
            ContentLayout.GeneratedWorldActorSpawnsPath(options.GeneratedRoot, worldId),
            WorldActorJson.SerializeSpawnRuntime(spawns),
            options.VerifyOnly,
            $"Compiled world actor spawns for World '{worldId}'");
        Console.WriteLine(
            "World actor spawns {0} for world {1} at revision {2} with {3} runtime instances.",
            options.VerifyOnly ? "verified" : "compiled",
            spawns.WorldId,
            spawns.Revision,
            spawns.SpawnInstances.Length);
    }

    GeneratedOutput.RemoveOrRejectStale(
        WorldActorContent.FindStaleOutputs(options.GeneratedRoot, worldIds),
        options.VerifyOnly,
        "Compiled world actor data");

    // The actor texts belong to the authored actor catalog. The compiled actors carry none.
    TextTableOutput.WriteOrVerify(
        options.GeneratedRoot,
        ContentText.FromWorldActors(catalogAuthoring),
        options.VerifyOnly);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("World actor compilation failed: " + exception.Message);
    return 1;
}

// Only the catalog refers to items, so its item references are checked once, not per World.
static WorldActorCatalogRuntimeDocument CompileCatalog(
    WorldActorCatalogAuthoringDocument authoring,
    ItemCatalogRuntimeDocument itemCatalog)
{
    try
    {
        var catalog = WorldActorCompiler.CompileCatalog(authoring);
        WorldActorItemReferences.Validate(catalog, itemCatalog);
        return catalog;
    }
    catch (WorldActorValidationException exception)
    {
        throw new InvalidDataException($"Actor catalog: {exception.Message}", exception);
    }
}

static WorldActorSpawnRuntimeDocument CompileSpawns(
    WorldActorCatalogRuntimeDocument catalog,
    string contentRoot,
    string worldId)
{
    try
    {
        return WorldActorCompiler.CompileSpawns(
            catalog,
            WorldActorContent.LoadSpawns(contentRoot, worldId));
    }
    catch (WorldActorValidationException exception)
    {
        throw new InvalidDataException($"World '{worldId}': {exception.Message}", exception);
    }
}

// Loot, vendor capabilities, and resource profiles reference item content, so the compiled
// item catalog must exist first, as the content compilers run in that order.
static ItemCatalogRuntimeDocument LoadItemCatalog(string generatedRoot)
{
    var path = ContentLayout.GeneratedItemCatalogPath(generatedRoot);
    var runtime = StrictContentJson.DeserializeFile<ItemCatalogRuntimeDocument>(
        path,
        "Compiled item catalog");
    return ItemCatalogRuntimeVerifier.VerifyAndCanonicalize(runtime);
}
