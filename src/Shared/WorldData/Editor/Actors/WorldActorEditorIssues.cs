using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor.ContentTools;

namespace Mmorpg.WorldData.Editor.Actors
{
    // Live validation of actor and spawn content with the shared WorldActorCompiler. Every
    // finding is keyed by its collection and entry id, such as "actors/npc.blacksmith" or
    // "spawnPoints/spawn.blacksmith.1", so Actor Studio and Spawn Authoring can each mark
    // the entries they list.
    public static class WorldActorEditorIssues
    {
        public const string Actors = "actors";
        public const string Factions = "factions";
        public const string PresentationArchetypes = "presentationArchetypes";
        public const string ActivityProfiles = "activityProfiles";
        public const string RespawnProfiles = "respawnProfiles";
        public const string SpawnPoints = "spawnPoints";
        public const string SpawnGroups = "spawnGroups";
        public const string SpawnAreas = "spawnAreas";
        public const string PatrolPaths = "patrolPaths";

        private static readonly Dictionary<string, string> CollectionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Factions] = "Faction",
            [PresentationArchetypes] = "Presentation archetype",
            [ActivityProfiles] = "Activity profile",
            [RespawnProfiles] = "Respawn profile",
            [SpawnPoints] = "Spawn point",
            [SpawnGroups] = "Spawn group",
            [SpawnAreas] = "Spawn area",
            [PatrolPaths] = "Patrol path"
        };

        public static string Key(string collection, string id)
        {
            return collection + "/" + (id ?? string.Empty);
        }

        // Collections of the spawn file rather than the actor catalog.
        public static bool IsSpawnCollection(string collection)
        {
            return collection == SpawnPoints
                || collection == SpawnGroups
                || collection == SpawnAreas
                || collection == PatrolPaths;
        }

        public static IReadOnlyList<ContentIssue> Collect(WorldActorEditorWorkspace workspace)
        {
            if (workspace == null)
            {
                return Array.Empty<ContentIssue>();
            }

            try
            {
                WorldActorCompiler.Compile(workspace.Catalog.ToDomain(), workspace.Spawns.ToDomain());
                return Array.Empty<ContentIssue>();
            }
            catch (WorldActorValidationException exception)
            {
                var ids = EntryIds(workspace);
                return exception.Errors.Select(error => ToIssue(error, ids)).ToArray();
            }
            catch (Exception exception)
            {
                return new[] { new ContentIssue(string.Empty, string.Empty, exception.Message) };
            }
        }

        // The collection and id of a key, for describing a finding.
        public static bool TrySplitKey(string key, out string collection, out string id)
        {
            collection = string.Empty;
            id = string.Empty;
            var separator = (key ?? string.Empty).IndexOf('/');
            if (separator <= 0)
            {
                return false;
            }

            collection = key.Substring(0, separator);
            id = key.Substring(separator + 1);
            return true;
        }

        // The finding with the entry it concerns, such as "Feral Wolf (mob.feral_wolf): ..." or
        // "Spawn area 'local.feral_wolves': ...".
        public static string Describe(ContentIssue issue, WorldActorEditorWorkspace workspace)
        {
            if (!TrySplitKey(issue.EntityKey, out var collection, out var id))
            {
                return issue.Message;
            }

            if (collection == Actors)
            {
                var actor = workspace?.Catalog.actors?.FirstOrDefault(entry => string.Equals(entry?.id, id, StringComparison.Ordinal));
                return (actor == null ? id : actor.displayName + " (" + id + ")") + ": " + issue.Message;
            }

            var label = CollectionLabels.TryGetValue(collection, out var value) ? value : collection;
            return label + " '" + id + "': " + issue.Message;
        }

        private static ContentIssue ToIssue(string error, IReadOnlyDictionary<string, string[]> ids)
        {
            if (ContentIssuePath.TryParse(error, out var collection, out var index, out var field, out var detail)
                && ids.TryGetValue(collection, out var entries)
                && index >= 0
                && index < entries.Length)
            {
                return new ContentIssue(Key(collection, entries[index]), field, detail);
            }

            if (ContentIssuePath.TryParseDuplicateId(error, out var duplicateCollection, out var id)
                && ids.ContainsKey(duplicateCollection))
            {
                return new ContentIssue(
                    Key(duplicateCollection, id),
                    "id",
                    "id '" + id + "' is used by more than one entry.");
            }

            return new ContentIssue(string.Empty, string.Empty, error);
        }

        private static IReadOnlyDictionary<string, string[]> EntryIds(WorldActorEditorWorkspace workspace)
        {
            var catalog = workspace.Catalog;
            var spawns = workspace.Spawns;
            return new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [Actors] = (catalog.actors ?? Array.Empty<WorldActorEditorDefinition>()).Select(entry => entry?.id).ToArray(),
                [Factions] = (catalog.factions ?? Array.Empty<WorldActorEditorFaction>()).Select(entry => entry?.id).ToArray(),
                [PresentationArchetypes] = (catalog.presentationArchetypes ?? Array.Empty<WorldActorEditorPresentation>()).Select(entry => entry?.id).ToArray(),
                [ActivityProfiles] = (catalog.activityProfiles ?? Array.Empty<WorldActorEditorActivityProfile>()).Select(entry => entry?.id).ToArray(),
                [RespawnProfiles] = (catalog.respawnProfiles ?? Array.Empty<WorldActorEditorRespawnProfile>()).Select(entry => entry?.id).ToArray(),
                [SpawnPoints] = (spawns.spawnPoints ?? Array.Empty<WorldActorEditorSpawnPoint>()).Select(entry => entry?.id).ToArray(),
                [SpawnGroups] = (spawns.spawnGroups ?? Array.Empty<WorldActorEditorSpawnGroup>()).Select(entry => entry?.id).ToArray(),
                [SpawnAreas] = (spawns.spawnAreas ?? Array.Empty<WorldActorEditorSpawnArea>()).Select(entry => entry?.id).ToArray(),
                [PatrolPaths] = (spawns.patrolPaths ?? Array.Empty<WorldActorEditorPatrolPath>()).Select(entry => entry?.id).ToArray()
            };
        }
    }
}
