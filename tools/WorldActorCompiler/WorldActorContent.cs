using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;

namespace Mmorpg.Tools.WorldActorCompiler;

// content/actors/catalog.json: factions, presentation, and the activity, respawn, combat,
// brain, locomotion, and loot profiles shared by every actor.
public sealed class WorldActorCatalogHeaderDocument
{
    public int FormatVersion;
    public string? CatalogId;
    public WorldActorFactionAuthoringEntry[]? Factions;
    public WorldActorPresentationAuthoringEntry[]? PresentationArchetypes;
    public WorldActorActivityProfileAuthoringEntry[]? ActivityProfiles;
    public WorldActorRespawnProfileAuthoringEntry[]? RespawnProfiles;
    public WorldActorResourceProfileAuthoringEntry[]? ResourceProfiles;
    public WorldActorCombatProfileAuthoringEntry[]? CombatProfiles;
    public WorldActorBrainProfileAuthoringEntry[]? BrainProfiles;
    public WorldActorLocomotionProfileAuthoringEntry[]? LocomotionProfiles;
    public WorldActorLootProfileAuthoringEntry[]? LootProfiles;
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
            CombatProfiles = header.CombatProfiles,
            BrainProfiles = header.BrainProfiles,
            LocomotionProfiles = header.LocomotionProfiles,
            LootProfiles = header.LootProfiles,
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

    // Everything in the compiled actor folders that a compile of these Worlds does not write: a
    // World folder that is no World, and any other file next to the catalog or a World's
    // spawns, such as a world-actors.json from an older format.
    public static string[] FindStaleOutputs(string generatedRoot, IReadOnlyCollection<string> worldIds)
    {
        var stale = new List<string>();
        stale.AddRange(GeneratedOutput.UnexpectedEntries(
            ContentLayout.GeneratedActorsDirectory(generatedRoot),
            [ContentLayout.ActorCatalogFileName]));
        stale.AddRange(GeneratedOutput.UnexpectedEntries(
            ContentLayout.GeneratedWorldsDirectory(generatedRoot),
            worldIds));
        foreach (var worldId in worldIds)
        {
            stale.AddRange(GeneratedOutput.UnexpectedEntries(
                Path.GetDirectoryName(ContentLayout.GeneratedWorldActorSpawnsPath(generatedRoot, worldId))!,
                [ContentLayout.WorldActorSpawnsFileName]));
        }

        return stale.Order(StringComparer.Ordinal).ToArray();
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
