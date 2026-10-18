using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Tools.WorldActorCompiler;

// content/actors/catalog.json: factions, presentation, and profiles shared by every actor.
public sealed class WorldActorCatalogHeaderDocument
{
    public int FormatVersion;
    public string? CatalogId;
    public WorldActorFactionAuthoringEntry[]? Factions;
    public WorldActorPresentationAuthoringEntry[]? PresentationArchetypes;
    public WorldActorActivityProfileAuthoringEntry[]? ActivityProfiles;
    public WorldActorRespawnProfileAuthoringEntry[]? RespawnProfiles;
    public WorldActorResourceProfileAuthoringEntry[]? ResourceProfiles;
}

// Assembles actor authoring from catalog.json, one file per actor definition, and the
// actor-spawns.json file of each World.
public static class WorldActorContent
{
    public static WorldActorCatalogAuthoringDocument LoadCatalog(string actorsDirectory)
    {
        var header = StrictContentJson.DeserializeFile<WorldActorCatalogHeaderDocument>(
            ContentLayout.CatalogPath(actorsDirectory),
            "World actor catalog");
        var actors = ContentLayout.ListDefinitionFiles(actorsDirectory)
            .Select(LoadDefinition)
            .ToArray();
        return new WorldActorCatalogAuthoringDocument
        {
            FormatVersion = header.FormatVersion,
            CatalogId = header.CatalogId,
            Factions = header.Factions,
            PresentationArchetypes = header.PresentationArchetypes,
            ActivityProfiles = header.ActivityProfiles,
            RespawnProfiles = header.RespawnProfiles,
            ResourceProfiles = header.ResourceProfiles,
            Actors = actors
        };
    }

    public static WorldActorSpawnAuthoringDocument LoadSpawns(string contentRoot, string worldId)
    {
        var path = ContentLayout.ActorSpawnsPath(contentRoot, worldId);
        var spawns = StrictContentJson.DeserializeFile<WorldActorSpawnAuthoringDocument>(
            path,
            "World actor spawns");
        if (!string.Equals(spawns.WorldId, worldId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World actor spawn file '{path}' must declare worldId '{worldId}'.");
        }

        return spawns;
    }

    public static string[] ListWorldIds(string contentRoot)
    {
        return ContentLayout.ListWorldIds(ContentLayout.WorldsDirectory(contentRoot));
    }

    private static WorldActorAuthoringEntry LoadDefinition(ContentDefinitionFile file)
    {
        var actor = StrictContentJson.DeserializeFile<WorldActorAuthoringEntry>(
            file.Path,
            "World actor definition");
        if (!string.Equals(actor.Id, file.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World actor definition file '{file.Path}' must contain id '{file.Id}'.");
        }

        return actor;
    }
}
