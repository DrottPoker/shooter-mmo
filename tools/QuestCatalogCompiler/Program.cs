using Mmorpg.Tools.ContentPipeline;
using Mmorpg.Tools.QuestCatalogCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;
using Mmorpg.WorldData.Text;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: QuestCatalogCompiler " + ContentToolOptions.Usage);
    return 1;
}

try
{
    var contentRoot = options!.ContentRoot;
    var authoring = QuestContent.LoadCatalog(ContentLayout.QuestsDirectory(contentRoot));
    var items = LoadItemCatalog(options.GeneratedRoot);
    var actors = LoadActorCatalog(options.GeneratedRoot);
    var worlds = LoadWorlds(contentRoot, options.GeneratedRoot, actors);
    var runtime = QuestCatalogCompiler.Compile(authoring, items, actors, worlds);
    GeneratedOutput.WriteOrVerifyText(
        ContentLayout.GeneratedQuestCatalogPath(options.GeneratedRoot),
        QuestJson.SerializeRuntime(runtime),
        options.VerifyOnly,
        "Compiled quest catalog");
    Console.WriteLine(
        "Quest catalog {0} for catalog {1} at revision {2} with {3} quests.",
        options.VerifyOnly ? "verified" : "compiled",
        runtime.CatalogId,
        runtime.Revision,
        runtime.Quests.Length);
    TextTableOutput.WriteOrVerify(
        options.GeneratedRoot,
        ContentText.FromQuestCatalog(authoring),
        options.VerifyOnly);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Quest catalog compilation failed: " + exception.Message);
    return 1;
}

// Quests reference items and actors, so the item and actor compilers must have run first.
static ItemCatalogRuntimeDocument LoadItemCatalog(string generatedRoot)
{
    var runtime = StrictContentJson.DeserializeFile<ItemCatalogRuntimeDocument>(
        ContentLayout.GeneratedItemCatalogPath(generatedRoot),
        "Compiled item catalog");
    return ItemCatalogRuntimeVerifier.VerifyAndCanonicalize(runtime);
}

static WorldActorCatalogRuntimeDocument LoadActorCatalog(string generatedRoot)
{
    var catalog = StrictContentJson.DeserializeFile<WorldActorCatalogRuntimeDocument>(
        ContentLayout.GeneratedActorCatalogPath(generatedRoot),
        "Compiled world actor catalog");
    WorldActorCompiler.ValidateRuntime(catalog);
    return catalog;
}

// Every World's compiled spawns, so a quest can only name actors that some World places.
static WorldActorSpawnRuntimeDocument[] LoadWorlds(
    string contentRoot,
    string generatedRoot,
    WorldActorCatalogRuntimeDocument actors)
{
    var worldIds = ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(contentRoot));
    if (worldIds.Length == 0)
    {
        throw new InvalidDataException(
            $"No World exists below '{ContentLayout.WorldsDirectory(contentRoot)}'.");
    }

    return worldIds
        .Select(worldId =>
        {
            var spawns = StrictContentJson.DeserializeFile<WorldActorSpawnRuntimeDocument>(
                ContentLayout.GeneratedWorldActorSpawnsPath(generatedRoot, worldId),
                "Compiled world actor spawns");
            WorldActorCompiler.ValidateRuntime(new WorldActorRuntime(actors, spawns));
            return spawns;
        })
        .ToArray();
}
