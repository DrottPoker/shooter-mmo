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

    public static WorldActorCatalogRuntimeDocument DeserializeCatalogRuntime(string json)
    {
        return StrictContentJson.Deserialize<WorldActorCatalogRuntimeDocument>(
            json,
            "World actor catalog runtime");
    }

    public static WorldActorSpawnRuntimeDocument DeserializeSpawnRuntime(string json)
    {
        return StrictContentJson.Deserialize<WorldActorSpawnRuntimeDocument>(
            json,
            "World actor spawn runtime");
    }

    public static string SerializeCatalogRuntime(WorldActorCatalogRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictContentJson.Serialize(document);
    }

    public static string SerializeSpawnRuntime(WorldActorSpawnRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictContentJson.Serialize(document);
    }
}
