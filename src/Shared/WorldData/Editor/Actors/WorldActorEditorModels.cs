using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Content;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    [Serializable]
    public sealed class WorldActorEditorCatalog
    {
        public int formatVersion;
        public string catalogId;
        public WorldActorEditorFaction[] factions;
        public WorldActorEditorPresentation[] presentationArchetypes;
        public WorldActorEditorActivityProfile[] activityProfiles;
        public WorldActorEditorRespawnProfile[] respawnProfiles;
        public WorldActorEditorDefinition[] actors;

        public WorldActorCatalogAuthoringDocument ToDomain()
        {
            return new WorldActorCatalogAuthoringDocument
            {
                FormatVersion = formatVersion,
                CatalogId = catalogId,
                Factions = (factions ?? Array.Empty<WorldActorEditorFaction>())
                    .Select(value => value.ToDomain()).ToArray(),
                PresentationArchetypes = (presentationArchetypes
                        ?? Array.Empty<WorldActorEditorPresentation>())
                    .Select(value => value.ToDomain()).ToArray(),
                ActivityProfiles = (activityProfiles
                        ?? Array.Empty<WorldActorEditorActivityProfile>())
                    .Select(value => value.ToDomain()).ToArray(),
                RespawnProfiles = (respawnProfiles
                        ?? Array.Empty<WorldActorEditorRespawnProfile>())
                    .Select(value => value.ToDomain()).ToArray(),
                Actors = (actors ?? Array.Empty<WorldActorEditorDefinition>())
                    .Select(value => value.ToDomain()).ToArray()
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorFaction
    {
        public string id;
        public string displayName;
        public string defaultDisposition;

        public WorldActorFactionAuthoringEntry ToDomain()
        {
            return new WorldActorFactionAuthoringEntry
            {
                Id = id,
                DisplayName = displayName,
                DefaultDisposition = defaultDisposition
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorPresentation
    {
        public string id;
        public string displayName;
        public string actorKind;

        public WorldActorPresentationAuthoringEntry ToDomain()
        {
            return new WorldActorPresentationAuthoringEntry
            {
                Id = id,
                DisplayName = displayName,
                ActorKind = actorKind
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorActivityProfile
    {
        public string id;
        public float activationRange;
        public float dormancyRange;
        public float activeTickRateHz;
        public float dormantTickRateHz;

        public WorldActorActivityProfileAuthoringEntry ToDomain()
        {
            return new WorldActorActivityProfileAuthoringEntry
            {
                Id = id,
                ActivationRange = activationRange,
                DormancyRange = dormancyRange,
                ActiveTickRateHz = activeTickRateHz,
                DormantTickRateHz = dormantTickRateHz
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorRespawnProfile
    {
        public string id;
        public float delaySeconds;
        public int populationLimit = 1;

        public WorldActorRespawnProfileAuthoringEntry ToDomain()
        {
            return new WorldActorRespawnProfileAuthoringEntry
            {
                Id = id,
                DelaySeconds = delaySeconds,
                PopulationLimit = populationLimit
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorDefinition
    {
        public string id;
        public string displayName;
        public string kind;
        public string factionId;
        public string disposition;
        public string presentationArchetypeId;
        public string damagePolicy;
        public string[] tags;
        public WorldActorEditorBounds interactionBounds;
        public WorldActorEditorCapability[] capabilities;
        public string activityProfileId;
        public string respawnProfileId;
        public string corpsePersistenceMode;
        public float corpseLifetimeSeconds;

        public WorldActorAuthoringEntry ToDomain()
        {
            return new WorldActorAuthoringEntry
            {
                Id = id,
                DisplayName = displayName,
                Kind = kind,
                FactionId = factionId,
                Disposition = disposition,
                PresentationArchetypeId = presentationArchetypeId,
                DamagePolicy = damagePolicy,
                Tags = tags ?? Array.Empty<string>(),
                InteractionBounds = (interactionBounds ?? new WorldActorEditorBounds())
                    .ToDomain(),
                Capabilities = (capabilities ?? Array.Empty<WorldActorEditorCapability>())
                    .Select(value => value.ToDomain()).ToArray(),
                ActivityProfileId = activityProfileId,
                RespawnProfileId = respawnProfileId,
                CorpsePersistenceMode = corpsePersistenceMode,
                CorpseLifetimeSeconds = corpseLifetimeSeconds
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorBounds
    {
        public float centerX;
        public float centerY;
        public float centerZ;
        public float sizeX = 0.8f;
        public float sizeY = 2f;
        public float sizeZ = 0.8f;

        public WorldActorBoundsAuthoringEntry ToDomain()
        {
            return new WorldActorBoundsAuthoringEntry
            {
                CenterX = centerX,
                CenterY = centerY,
                CenterZ = centerZ,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorCapability
    {
        public string id;
        public string kind;
        public string displayName;

        public WorldActorCapabilityAuthoringEntry ToDomain()
        {
            return new WorldActorCapabilityAuthoringEntry
            {
                Id = id,
                Kind = kind,
                DisplayName = displayName
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorSpawns
    {
        public int formatVersion;
        public string worldId;
        public string actorCatalogId;
        public WorldActorEditorSpawnPoint[] spawnPoints;
        public WorldActorEditorSpawnGroup[] spawnGroups;
        public WorldActorEditorSpawnArea[] spawnAreas;
        public WorldActorEditorPatrolPath[] patrolPaths;

        public WorldActorSpawnAuthoringDocument ToDomain()
        {
            return new WorldActorSpawnAuthoringDocument
            {
                FormatVersion = formatVersion,
                WorldId = worldId,
                ActorCatalogId = actorCatalogId,
                SpawnPoints = (spawnPoints ?? Array.Empty<WorldActorEditorSpawnPoint>())
                    .Select(value => value.ToDomain()).ToArray(),
                SpawnGroups = (spawnGroups ?? Array.Empty<WorldActorEditorSpawnGroup>())
                    .Select(value => value.ToDomain()).ToArray(),
                SpawnAreas = (spawnAreas ?? Array.Empty<WorldActorEditorSpawnArea>())
                    .Select(value => value.ToDomain()).ToArray(),
                PatrolPaths = (patrolPaths ?? Array.Empty<WorldActorEditorPatrolPath>())
                    .Select(value => value.ToDomain()).ToArray()
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorSpawnPoint
    {
        public string id;
        public string actorId;
        public float x;
        public float y;
        public float z;
        public float yawDegrees;
        public string patrolPathId;

        public WorldActorSpawnPointAuthoringEntry ToDomain()
        {
            return new WorldActorSpawnPointAuthoringEntry
            {
                Id = id,
                ActorId = actorId,
                X = x,
                Y = y,
                Z = z,
                YawDegrees = yawDegrees,
                PatrolPathId = patrolPathId
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorSpawnGroup
    {
        public string id;
        public string actorId;
        public float centerX;
        public float centerY;
        public float centerZ;
        public float yawDegrees;
        public int rows = 1;
        public int columns = 1;
        public float spacing = 2f;
        public string patrolPathId;

        public WorldActorSpawnGroupAuthoringEntry ToDomain()
        {
            return new WorldActorSpawnGroupAuthoringEntry
            {
                Id = id,
                ActorId = actorId,
                CenterX = centerX,
                CenterY = centerY,
                CenterZ = centerZ,
                YawDegrees = yawDegrees,
                Rows = rows,
                Columns = columns,
                Spacing = spacing,
                PatrolPathId = patrolPathId
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorSpawnArea
    {
        public string id;
        public string actorId;
        public float minimumX;
        public float minimumZ;
        public float maximumX;
        public float maximumZ;
        public float y;
        public float yawDegrees;
        public int count = 1;
        public int seed = 1;
        public string patrolPathId;

        public WorldActorSpawnAreaAuthoringEntry ToDomain()
        {
            return new WorldActorSpawnAreaAuthoringEntry
            {
                Id = id,
                ActorId = actorId,
                MinimumX = minimumX,
                MinimumZ = minimumZ,
                MaximumX = maximumX,
                MaximumZ = maximumZ,
                Y = y,
                YawDegrees = yawDegrees,
                Count = count,
                Seed = seed,
                PatrolPathId = patrolPathId
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorPatrolPath
    {
        public string id;
        public WorldActorEditorPatrolPoint[] points;

        public WorldActorPatrolPathAuthoringEntry ToDomain()
        {
            return new WorldActorPatrolPathAuthoringEntry
            {
                Id = id,
                Points = (points ?? Array.Empty<WorldActorEditorPatrolPoint>())
                    .Select(value => value.ToDomain()).ToArray()
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorPatrolPoint
    {
        public float x;
        public float y;
        public float z;
        public float waitSeconds;

        public WorldActorPatrolPointAuthoringEntry ToDomain()
        {
            return new WorldActorPatrolPointAuthoringEntry
            {
                X = x,
                Y = y,
                Z = z,
                WaitSeconds = waitSeconds
            };
        }
    }

    [Serializable]
    public sealed class WorldActorEditorRuntime
    {
        public string revision;
        public WorldActorEditorRuntimeEntry[] actors;
        public WorldActorEditorRuntimeEntry[] spawnSources;
        public WorldActorEditorRuntimeEntry[] spawnInstances;
    }

    [Serializable]
    public sealed class WorldActorEditorRuntimeEntry
    {
        public string id;
        public string structuralFingerprint;
    }

    // content/actors/catalog.json: every catalog field except the actors, which live in
    // one file each below definitions/.
    [Serializable]
    public sealed class WorldActorEditorCatalogHeader
    {
        public int formatVersion;
        public string catalogId;
        public WorldActorEditorFaction[] factions;
        public WorldActorEditorPresentation[] presentationArchetypes;
        public WorldActorEditorActivityProfile[] activityProfiles;
        public WorldActorEditorRespawnProfile[] respawnProfiles;
    }

    // The canonical file format is pretty-printed JsonUtility output with shortest float
    // text and LF line endings. The actor Editor models hold no double fields.
    public static class WorldActorEditorJson
    {
        public static WorldActorEditorCatalogHeader DeserializeCatalogHeader(string json)
        {
            return Deserialize<WorldActorEditorCatalogHeader>(json, "actor catalog");
        }

        public static WorldActorEditorDefinition DeserializeDefinition(string json)
        {
            return Deserialize<WorldActorEditorDefinition>(json, "actor definition");
        }

        public static WorldActorEditorSpawns DeserializeSpawns(string json)
        {
            return Deserialize<WorldActorEditorSpawns>(json, "actor spawns");
        }

        public static WorldActorEditorRuntime DeserializeRuntime(string json)
        {
            return Deserialize<WorldActorEditorRuntime>(json, "actor runtime");
        }

        public static string SerializeCatalogHeader(WorldActorEditorCatalogHeader header)
        {
            return Serialize(header);
        }

        public static string SerializeDefinition(WorldActorEditorDefinition definition)
        {
            return Serialize(definition);
        }

        public static string SerializeSpawns(WorldActorEditorSpawns document)
        {
            return Serialize(document);
        }

        public static T Clone<T>(T document)
        {
            return Deserialize<T>(Serialize(document), typeof(T).Name);
        }

        private static T Deserialize<T>(string json, string label)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("World " + label + " JSON is empty.");
            }

            try
            {
                var value = JsonUtility.FromJson<T>(json);
                if (value == null)
                {
                    throw new InvalidDataException(
                        "World " + label + " JSON has no root object.");
                }

                return value;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "World " + label + " JSON is malformed: " + exception.Message,
                    exception);
            }
        }

        private static string Serialize<T>(T document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            return SinglePrecisionJson.ShortenFloats(JsonUtility.ToJson(document, true))
                .Replace("\r\n", "\n") + "\n";
        }
    }

    // Reads and writes content/actors: catalog.json plus one file per actor definition.
    public static class WorldActorEditorContent
    {
        public static WorldActorEditorCatalog Load(string actorsDirectory)
        {
            var catalogPath = ContentLayout.CatalogPath(actorsDirectory);
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("World actor catalog file is missing.", catalogPath);
            }

            var header = WorldActorEditorJson.DeserializeCatalogHeader(
                File.ReadAllText(catalogPath));
            return new WorldActorEditorCatalog
            {
                formatVersion = header.formatVersion,
                catalogId = header.catalogId,
                factions = header.factions,
                presentationArchetypes = header.presentationArchetypes,
                activityProfiles = header.activityProfiles,
                respawnProfiles = header.respawnProfiles,
                actors = ContentLayout.ListDefinitionFiles(actorsDirectory)
                    .Select(LoadDefinition)
                    .ToArray()
            };
        }

        // File text keyed by path relative to the actors directory.
        public static Dictionary<string, string> CreateFiles(WorldActorEditorCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContentLayout.CatalogFileName] = WorldActorEditorJson.SerializeCatalogHeader(
                    new WorldActorEditorCatalogHeader
                    {
                        formatVersion = catalog.formatVersion,
                        catalogId = catalog.catalogId,
                        factions = catalog.factions ?? Array.Empty<WorldActorEditorFaction>(),
                        presentationArchetypes = catalog.presentationArchetypes
                            ?? Array.Empty<WorldActorEditorPresentation>(),
                        activityProfiles = catalog.activityProfiles
                            ?? Array.Empty<WorldActorEditorActivityProfile>(),
                        respawnProfiles = catalog.respawnProfiles
                            ?? Array.Empty<WorldActorEditorRespawnProfile>()
                    })
            };
            foreach (var actor in catalog.actors ?? Array.Empty<WorldActorEditorDefinition>())
            {
                var path = Path.Combine(
                    ContentLayout.DefinitionsDirectoryName,
                    ContentLayout.DefinitionFileName(actor.id));
                if (files.ContainsKey(path))
                {
                    throw new InvalidDataException(
                        "World actor id '" + actor.id + "' is used more than once.");
                }

                files.Add(path, WorldActorEditorJson.SerializeDefinition(actor));
            }

            return files;
        }

        private static WorldActorEditorDefinition LoadDefinition(ContentDefinitionFile file)
        {
            WorldActorEditorDefinition actor;
            try
            {
                actor = WorldActorEditorJson.DeserializeDefinition(File.ReadAllText(file.Path));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(file.Path + ": " + exception.Message, exception);
            }

            if (!string.Equals(actor.id, file.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "World actor definition file '" + file.Path + "' must contain id '"
                    + file.Id + "'.");
            }

            return actor;
        }
    }
}
