using System;
using System.IO;
using System.Linq;
using ShooterMmo.WorldData.Actors;
using UnityEngine;

namespace ShooterMmo.WorldData.Editor.Actors
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

    public static class WorldActorEditorJson
    {
        public static WorldActorEditorCatalog DeserializeCatalog(string json)
        {
            return Deserialize<WorldActorEditorCatalog>(json, "actor catalog");
        }

        public static WorldActorEditorSpawns DeserializeSpawns(string json)
        {
            return Deserialize<WorldActorEditorSpawns>(json, "actor spawns");
        }

        public static WorldActorEditorRuntime DeserializeRuntime(string json)
        {
            return Deserialize<WorldActorEditorRuntime>(json, "actor runtime");
        }

        public static string SerializeCatalog(WorldActorEditorCatalog document)
        {
            return Serialize(document);
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

            return JsonUtility.ToJson(document, true).Replace("\r\n", "\n") + "\n";
        }
    }
}
