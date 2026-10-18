using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: WorldActorCompiler " + ContentToolOptions.Usage);
    return 1;
}

try
{
    var contentRoot = options!.ContentRoot;
    var catalog = WorldActorContent.LoadCatalog(ContentLayout.ActorsDirectory(contentRoot));
    var itemCatalog = LoadItemCatalog(options.GeneratedRoot);
    var worldIds = WorldActorContent.ListWorldIds(contentRoot);
    if (worldIds.Length == 0)
    {
        throw new InvalidDataException(
            $"No World exists below '{ContentLayout.WorldsDirectory(contentRoot)}'.");
    }

    foreach (var worldId in worldIds)
    {
        var runtime = CompileWorld(catalog, itemCatalog, contentRoot, worldId);
        GeneratedOutput.WriteOrVerifyText(
            ContentLayout.GeneratedWorldActorsPath(options.GeneratedRoot, worldId),
            WorldActorJson.SerializeRuntime(runtime),
            options.VerifyOnly,
            $"Compiled world actors for World '{worldId}'");
        Console.WriteLine(
            "World actors {0} for world {1} at revision {2} with {3} definitions and {4} runtime instances.",
            options.VerifyOnly ? "verified" : "compiled",
            runtime.WorldId,
            runtime.Revision,
            runtime.Actors.Length,
            runtime.SpawnInstances.Length);
    }

    GeneratedOutput.RemoveOrRejectStale(
        FindStaleRuntimeFiles(options.GeneratedRoot, worldIds),
        options.VerifyOnly,
        "Compiled world actors");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("World actor compilation failed: " + exception.Message);
    return 1;
}

static WorldActorRuntimeDocument CompileWorld(
    WorldActorCatalogAuthoringDocument catalog,
    ItemCatalogRuntimeDocument itemCatalog,
    string contentRoot,
    string worldId)
{
    try
    {
        var runtime = WorldActorCompiler.Compile(
            catalog,
            WorldActorContent.LoadSpawns(contentRoot, worldId));
        WorldActorItemReferences.Validate(runtime, itemCatalog);
        return runtime;
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

static string[] FindStaleRuntimeFiles(string generatedRoot, string[] worldIds)
{
    var directory = ContentLayout.GeneratedWorldsDirectory(generatedRoot);
    if (!Directory.Exists(directory))
    {
        return [];
    }

    return Directory.EnumerateDirectories(directory)
        .Where(path => !worldIds.Contains(Path.GetFileName(path), StringComparer.Ordinal))
        .Select(path => Path.Combine(path, ContentLayout.WorldActorsFileName))
        .Where(File.Exists)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
