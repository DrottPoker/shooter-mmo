using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.Tools.WorldActorCompiler;

public static class WorldActorJson
{
    public static WorldActorCatalogHeaderDocument DeserializeCatalog(string json)
    {
        return StrictContentJson.Deserialize<WorldActorCatalogHeaderDocument>(
            json,
            "World actor catalog");
    }

    public static WorldActorAuthoringEntry DeserializeDefinition(string json)
    {
        return StrictContentJson.Deserialize<WorldActorAuthoringEntry>(
            json,
            "World actor definition");
    }

    public static WorldActorSpawnAuthoringDocument DeserializeSpawns(string json)
    {
        return StrictContentJson.Deserialize<WorldActorSpawnAuthoringDocument>(
            json,
            "World actor spawns");
    }

    public static WorldActorRuntimeDocument DeserializeRuntime(string json)
    {
        return StrictContentJson.Deserialize<WorldActorRuntimeDocument>(
            json,
            "World actor runtime");
    }

    public static string SerializeRuntime(WorldActorRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictContentJson.Serialize(document);
    }
}
