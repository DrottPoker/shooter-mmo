using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShooterMmo.WorldData.Actors
{
    public sealed class WorldActorValidationException : Exception
    {
        public WorldActorValidationException(IReadOnlyCollection<string> errors)
            : base(CreateMessage(errors))
        {
            Errors = errors.ToArray();
        }

        public IReadOnlyList<string> Errors { get; }

        private static string CreateMessage(IReadOnlyCollection<string> errors)
        {
            return "World actor validation failed:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => "- " + error));
        }
    }

    public static partial class WorldActorCompiler
    {
        private const int MaximumIdentifierLength = 128;
        private const int MaximumDisplayNameLength = 128;
        private const int MaximumInstancesPerSource = 256;
        private const int MaximumRuntimeInstances = 32768;
        private const int MaximumDefinitionsPerCollection = 4096;
        private const int MaximumSpawnSources = 8192;
        private const int MaximumPatrolPoints = 256;
        private const int MaximumCapabilitiesPerActor = 16;
        private const int MaximumTagsPerActor = 32;
        private const float MaximumWorldCoordinateMagnitude = 1_000_000f;
        private const float MaximumInteractionBoundsSize = 1_000_000f;
        private const float MaximumActivityRange = 2000f;
        private const float MaximumActiveTickRateHz = 30f;
        private const float MaximumDormantTickRateHz = 2f;

        private static readonly HashSet<string> SupportedActorKinds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorldActorKindIds.Npc,
                WorldActorKindIds.Mob
            };

        private static readonly HashSet<string> SupportedDispositions =
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorldActorDispositionIds.Friendly,
                WorldActorDispositionIds.Neutral,
                WorldActorDispositionIds.Hostile
            };

        private static readonly HashSet<string> SupportedDamagePolicies =
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorldActorDamagePolicyIds.Invulnerable,
                WorldActorDamagePolicyIds.Damageable
            };

        private static readonly HashSet<string> SupportedCapabilityKinds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorldActorCapabilityKindIds.Dialogue,
                WorldActorCapabilityKindIds.Vendor,
                WorldActorCapabilityKindIds.QuestOffer,
                WorldActorCapabilityKindIds.QuestTurnIn,
                WorldActorCapabilityKindIds.Crafting,
                WorldActorCapabilityKindIds.Insurance,
                WorldActorCapabilityKindIds.Trainer,
                WorldActorCapabilityKindIds.Bank,
                WorldActorCapabilityKindIds.RecoveryStorage
            };

        private static readonly HashSet<string> SupportedSpawnKinds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorldActorSpawnKindIds.Point,
                WorldActorSpawnKindIds.Group,
                WorldActorSpawnKindIds.Area
            };

        public static WorldActorRuntimeDocument Compile(
            WorldActorCatalogAuthoringDocument actorAuthoring,
            WorldActorSpawnAuthoringDocument spawnAuthoring)
        {
            if (actorAuthoring == null)
            {
                throw new ArgumentNullException(nameof(actorAuthoring));
            }

            if (spawnAuthoring == null)
            {
                throw new ArgumentNullException(nameof(spawnAuthoring));
            }

            Validate(actorAuthoring, spawnAuthoring);

            var runtime = new WorldActorRuntimeDocument
            {
                FormatVersion = WorldActorDataFormat.Version,
                CatalogId = actorAuthoring.CatalogId,
                WorldId = spawnAuthoring.WorldId,
                Revision = string.Empty,
                Factions = actorAuthoring.Factions
                    .Select(CompileFaction)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray(),
                PresentationArchetypes = actorAuthoring.PresentationArchetypes
                    .Select(CompilePresentation)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray(),
                ActivityProfiles = actorAuthoring.ActivityProfiles
                    .Select(CompileActivityProfile)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray(),
                RespawnProfiles = actorAuthoring.RespawnProfiles
                    .Select(CompileRespawnProfile)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray(),
                Actors = actorAuthoring.Actors
                    .Select(CompileActor)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray(),
                PatrolPaths = spawnAuthoring.PatrolPaths
                    .Select(CompilePatrolPath)
                    .OrderBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray()
            };

            var spawnSources = new List<WorldActorSpawnSourceDefinition>();
            var spawnInstances = new List<WorldActorSpawnInstanceDefinition>();
            foreach (var point in spawnAuthoring.SpawnPoints)
            {
                CompilePoint(point, spawnSources, spawnInstances);
            }

            foreach (var group in spawnAuthoring.SpawnGroups)
            {
                CompileGroup(group, spawnSources, spawnInstances);
            }

            foreach (var area in spawnAuthoring.SpawnAreas)
            {
                CompileArea(area, spawnSources, spawnInstances);
            }

            runtime.SpawnSources = spawnSources
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            runtime.SpawnInstances = spawnInstances
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            runtime.Revision = ComputeRuntimeRevision(runtime);
            ValidateRuntime(runtime);
            return runtime;
        }

        public static string ComputeSha256Hex(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            using (var sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(bytes));
            }
        }

        private static void Validate(
            WorldActorCatalogAuthoringDocument actors,
            WorldActorSpawnAuthoringDocument spawns)
        {
            var errors = new List<string>();
            if (actors.FormatVersion != WorldActorDataFormat.Version)
            {
                errors.Add("actor formatVersion is unsupported.");
            }

            if (spawns.FormatVersion != WorldActorDataFormat.Version)
            {
                errors.Add("spawn formatVersion is unsupported.");
            }

            ValidateIdentifier(actors.CatalogId, "catalogId", errors);
            ValidateIdentifier(spawns.WorldId, "worldId", errors);
            ValidateIdentifier(spawns.ActorCatalogId, "actorCatalogId", errors);
            if (!string.IsNullOrWhiteSpace(actors.CatalogId)
                && !string.Equals(
                    actors.CatalogId,
                    spawns.ActorCatalogId,
                    StringComparison.Ordinal))
            {
                errors.Add("actorCatalogId must match catalogId.");
            }

            ValidateCollectionLimit(
                actors.Factions,
                MaximumDefinitionsPerCollection,
                "factions",
                errors);
            ValidateCollectionLimit(
                actors.PresentationArchetypes,
                MaximumDefinitionsPerCollection,
                "presentationArchetypes",
                errors);
            ValidateCollectionLimit(
                actors.ActivityProfiles,
                MaximumDefinitionsPerCollection,
                "activityProfiles",
                errors);
            ValidateCollectionLimit(
                actors.RespawnProfiles,
                MaximumDefinitionsPerCollection,
                "respawnProfiles",
                errors);
            ValidateCollectionLimit(
                actors.Actors,
                MaximumDefinitionsPerCollection,
                "actors",
                errors);
            ValidateCollectionLimit(
                spawns.PatrolPaths,
                MaximumSpawnSources,
                "patrolPaths",
                errors);

            var factionIds = ValidateFactions(actors.Factions, errors);
            var presentationIds = ValidatePresentations(
                actors.PresentationArchetypes,
                errors);
            var activityProfileIds = ValidateActivityProfiles(
                actors.ActivityProfiles,
                errors);
            var respawnProfileIds = ValidateRespawnProfiles(
                actors.RespawnProfiles,
                errors);
            var actorKinds = ValidateActors(
                actors.Actors,
                factionIds,
                presentationIds,
                activityProfileIds,
                respawnProfileIds,
                errors);
            var patrolPathIds = ValidatePatrolPaths(spawns.PatrolPaths, errors);
            ValidateSpawns(spawns, actorKinds, patrolPathIds, errors);

            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }

        private static HashSet<string> ValidateFactions(
            WorldActorFactionAuthoringEntry[] values,
            ICollection<string> errors)
        {
            var ids = RequireCollection(values, "factions", errors);
            if (values == null)
            {
                return ids;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var path = "factions[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                AddIdentifier(ids, value.Id, path + ".id", errors);
                ValidateDisplayName(value.DisplayName, path + ".displayName", errors);
                ValidateSupported(
                    value.DefaultDisposition,
                    SupportedDispositions,
                    path + ".defaultDisposition",
                    errors);
            }

            return ids;
        }

        private static Dictionary<string, string> ValidatePresentations(
            WorldActorPresentationAuthoringEntry[] values,
            ICollection<string> errors)
        {
            var kindsById = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values == null || values.Length == 0)
            {
                errors.Add("presentationArchetypes must contain at least one entry.");
                return kindsById;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var path = "presentationArchetypes[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(value.Id, path + ".id", errors);
                ValidateDisplayName(value.DisplayName, path + ".displayName", errors);
                ValidateSupported(value.ActorKind, SupportedActorKinds, path + ".actorKind", errors);
                if (!string.IsNullOrWhiteSpace(value.Id)
                    && !kindsById.TryAdd(value.Id, value.ActorKind))
                {
                    errors.Add("presentationArchetypes contains duplicate id '" + value.Id + "'.");
                }
            }

            return kindsById;
        }

        private static HashSet<string> ValidateActivityProfiles(
            WorldActorActivityProfileAuthoringEntry[] values,
            ICollection<string> errors)
        {
            var ids = RequireCollection(values, "activityProfiles", errors);
            if (values == null)
            {
                return ids;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var path = "activityProfiles[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                AddIdentifier(ids, value.Id, path + ".id", errors);
                ValidatePositiveFinite(value.ActivationRange, path + ".activationRange", errors);
                ValidatePositiveFinite(value.DormancyRange, path + ".dormancyRange", errors);
                ValidatePositiveFinite(value.ActiveTickRateHz, path + ".activeTickRateHz", errors);
                ValidatePositiveFinite(value.DormantTickRateHz, path + ".dormantTickRateHz", errors);
                if (IsFinite(value.ActivationRange)
                    && IsFinite(value.DormancyRange)
                    && value.DormancyRange <= value.ActivationRange)
                {
                    errors.Add(path + ".dormancyRange must be greater than activationRange.");
                }

                if (value.ActivationRange > MaximumActivityRange
                    || value.DormancyRange > MaximumActivityRange
                    || value.ActiveTickRateHz > MaximumActiveTickRateHz
                    || value.DormantTickRateHz > MaximumDormantTickRateHz
                    || value.DormantTickRateHz > value.ActiveTickRateHz)
                {
                    errors.Add(path + " exceeds the supported scheduler bounds.");
                }
            }

            return ids;
        }

        private static HashSet<string> ValidateRespawnProfiles(
            WorldActorRespawnProfileAuthoringEntry[] values,
            ICollection<string> errors)
        {
            var ids = RequireCollection(values, "respawnProfiles", errors);
            if (values == null)
            {
                return ids;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var path = "respawnProfiles[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                AddIdentifier(ids, value.Id, path + ".id", errors);
                if (!IsFinite(value.DelaySeconds) || value.DelaySeconds < 0f)
                {
                    errors.Add(path + ".delaySeconds must be finite and non-negative.");
                }

                if (value.PopulationLimit < 1
                    || value.PopulationLimit > MaximumInstancesPerSource)
                {
                    errors.Add(path + ".populationLimit must be between 1 and "
                        + MaximumInstancesPerSource + ".");
                }
            }

            return ids;
        }

        private static Dictionary<string, string> ValidateActors(
            WorldActorAuthoringEntry[] values,
            ISet<string> factionIds,
            IReadOnlyDictionary<string, string> presentationKinds,
            ISet<string> activityProfileIds,
            ISet<string> respawnProfileIds,
            ICollection<string> errors)
        {
            var kindsById = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values == null || values.Length == 0)
            {
                errors.Add("actors must contain at least one entry.");
                return kindsById;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var actor = values[index];
                var path = "actors[" + index + "]";
                if (actor == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateIdentifier(actor.Id, path + ".id", errors);
                ValidateDisplayName(actor.DisplayName, path + ".displayName", errors);
                ValidateSupported(actor.Kind, SupportedActorKinds, path + ".kind", errors);
                ValidateReference(actor.FactionId, factionIds, path + ".factionId", errors);
                ValidateSupported(actor.Disposition, SupportedDispositions, path + ".disposition", errors);
                ValidateReference(
                    actor.PresentationArchetypeId,
                    presentationKinds.Keys,
                    path + ".presentationArchetypeId",
                    errors);
                if (presentationKinds.TryGetValue(actor.PresentationArchetypeId ?? string.Empty, out var presentationKind)
                    && !string.Equals(presentationKind, actor.Kind, StringComparison.Ordinal))
                {
                    errors.Add(path + ".presentationArchetypeId must support actor kind '" + actor.Kind + "'.");
                }

                ValidateSupported(actor.DamagePolicy, SupportedDamagePolicies, path + ".damagePolicy", errors);
                if (string.Equals(actor.Kind, WorldActorKindIds.Npc, StringComparison.Ordinal)
                    && !string.Equals(
                        actor.DamagePolicy,
                        WorldActorDamagePolicyIds.Invulnerable,
                        StringComparison.Ordinal))
                {
                    errors.Add(path + " Phase 12 NPC definitions must be invulnerable.");
                }

                ValidateTags(actor.Tags, path + ".tags", errors);
                ValidateBounds(actor.InteractionBounds, path + ".interactionBounds", errors);
                ValidateCapabilities(actor.Capabilities, path + ".capabilities", errors);
                if (string.Equals(actor.Kind, WorldActorKindIds.Mob, StringComparison.Ordinal))
                {
                    if (actor.Capabilities != null && actor.Capabilities.Length > 0)
                    {
                        errors.Add(path + ".capabilities must be empty for a Phase 12 Mob.");
                    }

                    ValidateReference(
                        actor.ActivityProfileId,
                        activityProfileIds,
                        path + ".activityProfileId",
                        errors);
                    ValidateReference(
                        actor.RespawnProfileId,
                        respawnProfileIds,
                        path + ".respawnProfileId",
                        errors);
                }
                else
                {
                    ValidateOptionalEmpty(actor.ActivityProfileId, path + ".activityProfileId", errors);
                    ValidateOptionalEmpty(actor.RespawnProfileId, path + ".respawnProfileId", errors);
                }

                if (!string.IsNullOrWhiteSpace(actor.Id)
                    && !kindsById.TryAdd(actor.Id, actor.Kind))
                {
                    errors.Add("actors contains duplicate id '" + actor.Id + "'.");
                }
            }

            return kindsById;
        }

        private static void ValidateBounds(
            WorldActorBoundsAuthoringEntry bounds,
            string path,
            ICollection<string> errors)
        {
            if (bounds == null)
            {
                errors.Add(path + " is required.");
                return;
            }

            ValidateCoordinate(bounds.CenterX, path + ".centerX", errors);
            ValidateCoordinate(bounds.CenterY, path + ".centerY", errors);
            ValidateCoordinate(bounds.CenterZ, path + ".centerZ", errors);
            ValidatePositiveFinite(
                bounds.SizeX,
                MaximumInteractionBoundsSize,
                path + ".sizeX",
                errors);
            ValidatePositiveFinite(
                bounds.SizeY,
                MaximumInteractionBoundsSize,
                path + ".sizeY",
                errors);
            ValidatePositiveFinite(
                bounds.SizeZ,
                MaximumInteractionBoundsSize,
                path + ".sizeZ",
                errors);
        }

        private static void ValidateCapabilities(
            WorldActorCapabilityAuthoringEntry[] values,
            string path,
            ICollection<string> errors)
        {
            if (values == null)
            {
                errors.Add(path + " is required. Use an empty array when the actor has no capabilities.");
                return;
            }

            if (values.Length > MaximumCapabilitiesPerActor)
            {
                errors.Add(path + " cannot contain more than "
                    + MaximumCapabilitiesPerActor + " capabilities.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var capabilityPath = path + "[" + index + "]";
                if (value == null)
                {
                    errors.Add(capabilityPath + " must not be null.");
                    continue;
                }

                AddIdentifier(ids, value.Id, capabilityPath + ".id", errors);
                ValidateSupported(value.Kind, SupportedCapabilityKinds, capabilityPath + ".kind", errors);
                ValidateDisplayName(value.DisplayName, capabilityPath + ".displayName", errors);
            }
        }

        private static void ValidateTags(
            string[] values,
            string path,
            ICollection<string> errors)
        {
            if (values == null)
            {
                errors.Add(path + " is required. Use an empty array when the actor has no tags.");
                return;
            }

            if (values.Length > MaximumTagsPerActor)
            {
                errors.Add(path + " cannot contain more than "
                    + MaximumTagsPerActor + " tags.");
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                AddIdentifier(unique, values[index], path + "[" + index + "]", errors);
            }
        }

        private static HashSet<string> ValidatePatrolPaths(
            WorldActorPatrolPathAuthoringEntry[] values,
            ICollection<string> errors)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (values == null)
            {
                errors.Add("patrolPaths is required. Use an empty array when no paths exist.");
                return ids;
            }

            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var path = "patrolPaths[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                AddIdentifier(ids, value.Id, path + ".id", errors);
                if (value.Points == null || value.Points.Length < 2)
                {
                    errors.Add(path + ".points must contain at least two points.");
                    continue;
                }

                if (value.Points.Length > MaximumPatrolPoints)
                {
                    errors.Add(path + ".points cannot contain more than "
                        + MaximumPatrolPoints + " points.");
                }

                for (var pointIndex = 0; pointIndex < value.Points.Length; pointIndex++)
                {
                    var point = value.Points[pointIndex];
                    var pointPath = path + ".points[" + pointIndex + "]";
                    if (point == null)
                    {
                        errors.Add(pointPath + " must not be null.");
                        continue;
                    }

                    ValidateTransform(point.X, point.Y, point.Z, pointPath, errors);
                    if (!IsFinite(point.WaitSeconds) || point.WaitSeconds < 0f)
                    {
                        errors.Add(pointPath + ".waitSeconds must be finite and non-negative.");
                    }
                }
            }

            return ids;
        }

        private static void ValidateSpawns(
            WorldActorSpawnAuthoringDocument spawns,
            IReadOnlyDictionary<string, string> actorKinds,
            ISet<string> patrolPathIds,
            ICollection<string> errors)
        {
            if (spawns.SpawnPoints == null
                || spawns.SpawnGroups == null
                || spawns.SpawnAreas == null)
            {
                errors.Add("spawnPoints, spawnGroups, and spawnAreas are required arrays.");
                return;
            }


            var sourceCount = (long)spawns.SpawnPoints.Length
                + spawns.SpawnGroups.Length
                + spawns.SpawnAreas.Length;
            if (sourceCount > MaximumSpawnSources)
            {
                errors.Add("World actor spawn content cannot contain more than "
                    + MaximumSpawnSources + " sources.");
            }

            var instanceCount = (long)spawns.SpawnPoints.Length;
            foreach (var group in spawns.SpawnGroups)
            {
                if (group == null || group.Rows <= 0 || group.Columns <= 0)
                {
                    continue;
                }

                var groupCount = (long)group.Rows * group.Columns;
                instanceCount = groupCount > MaximumRuntimeInstances
                    || instanceCount > MaximumRuntimeInstances - groupCount
                    ? MaximumRuntimeInstances + 1L
                    : instanceCount + groupCount;
            }

            foreach (var area in spawns.SpawnAreas)
            {
                if (area == null || area.Count <= 0)
                {
                    continue;
                }

                instanceCount = instanceCount > MaximumRuntimeInstances - (long)area.Count
                    ? MaximumRuntimeInstances + 1L
                    : instanceCount + area.Count;
            }
            if (instanceCount > MaximumRuntimeInstances)
            {
                errors.Add("World actor spawn content cannot compile more than "
                    + MaximumRuntimeInstances + " runtime instances.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < spawns.SpawnPoints.Length; index++)
            {
                var value = spawns.SpawnPoints[index];
                var path = "spawnPoints[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateSpawnIdentity(ids, value.Id, value.ActorId, actorKinds, path, errors);
                ValidateTransform(value.X, value.Y, value.Z, path, errors);
                ValidateYaw(value.YawDegrees, path + ".yawDegrees", errors);
                ValidatePatrolReference(value.ActorId, value.PatrolPathId, actorKinds, patrolPathIds, path, errors);
            }

            for (var index = 0; index < spawns.SpawnGroups.Length; index++)
            {
                var value = spawns.SpawnGroups[index];
                var path = "spawnGroups[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateSpawnIdentity(ids, value.Id, value.ActorId, actorKinds, path, errors);
                ValidateTransform(value.CenterX, value.CenterY, value.CenterZ, path, errors);
                ValidateYaw(value.YawDegrees, path + ".yawDegrees", errors);
                if (value.Rows <= 0 || value.Columns <= 0
                    || (long)value.Rows * value.Columns > MaximumInstancesPerSource)
                {
                    errors.Add(path + " rows and columns must produce between 1 and "
                        + MaximumInstancesPerSource + " instances.");
                }

                ValidatePositiveFinite(value.Spacing, path + ".spacing", errors);
                ValidatePatrolReference(value.ActorId, value.PatrolPathId, actorKinds, patrolPathIds, path, errors);
            }

            for (var index = 0; index < spawns.SpawnAreas.Length; index++)
            {
                var value = spawns.SpawnAreas[index];
                var path = "spawnAreas[" + index + "]";
                if (value == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                ValidateSpawnIdentity(ids, value.Id, value.ActorId, actorKinds, path, errors);
                ValidateTransform(value.MinimumX, value.Y, value.MinimumZ, path + ".minimum", errors);
                ValidateTransform(value.MaximumX, value.Y, value.MaximumZ, path + ".maximum", errors);
                ValidateYaw(value.YawDegrees, path + ".yawDegrees", errors);
                if (IsFinite(value.MinimumX)
                    && IsFinite(value.MaximumX)
                    && IsFinite(value.MinimumZ)
                    && IsFinite(value.MaximumZ)
                    && (value.MaximumX <= value.MinimumX || value.MaximumZ <= value.MinimumZ))
                {
                    errors.Add(path + " bounds must have positive X and Z size.");
                }

                if (value.Count <= 0 || value.Count > MaximumInstancesPerSource)
                {
                    errors.Add(path + ".count must be between 1 and " + MaximumInstancesPerSource + ".");
                }

                ValidatePatrolReference(value.ActorId, value.PatrolPathId, actorKinds, patrolPathIds, path, errors);
            }

            if (ids.Count == 0)
            {
                errors.Add("At least one spawn point, group, or area is required.");
            }
        }

        private static void ValidateSpawnIdentity(
            ISet<string> ids,
            string id,
            string actorId,
            IReadOnlyDictionary<string, string> actorKinds,
            string path,
            ICollection<string> errors)
        {
            AddIdentifier(ids, id, path + ".id", errors);
            ValidateReference(actorId, actorKinds.Keys, path + ".actorId", errors);
        }

        private static void ValidatePatrolReference(
            string actorId,
            string patrolPathId,
            IReadOnlyDictionary<string, string> actorKinds,
            ISet<string> patrolPathIds,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrEmpty(patrolPathId))
            {
                return;
            }

            ValidateReference(patrolPathId, patrolPathIds, path + ".patrolPathId", errors);
            if (actorKinds.TryGetValue(actorId ?? string.Empty, out var kind)
                && !string.Equals(kind, WorldActorKindIds.Mob, StringComparison.Ordinal))
            {
                errors.Add(path + ".patrolPathId can only be assigned to a Mob spawn.");
            }
        }

        private static void ValidateCollectionLimit<T>(
            T[] values,
            int maximum,
            string path,
            ICollection<string> errors)
        {
            if (values != null && values.Length > maximum)
            {
                errors.Add(path + " cannot contain more than " + maximum + " entries.");
            }
        }

        private static WorldActorFactionDefinition CompileFaction(
            WorldActorFactionAuthoringEntry value)
        {
            var compiled = new WorldActorFactionDefinition
            {
                Id = value.Id,
                DisplayName = value.DisplayName,
                DefaultDisposition = value.DefaultDisposition
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                compiled.DisplayName,
                compiled.DefaultDisposition);
            return compiled;
        }

        private static WorldActorPresentationDefinition CompilePresentation(
            WorldActorPresentationAuthoringEntry value)
        {
            var compiled = new WorldActorPresentationDefinition
            {
                Id = value.Id,
                DisplayName = value.DisplayName,
                ActorKind = value.ActorKind
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                compiled.DisplayName,
                compiled.ActorKind);
            return compiled;
        }

        private static WorldActorActivityProfileDefinition CompileActivityProfile(
            WorldActorActivityProfileAuthoringEntry value)
        {
            var compiled = new WorldActorActivityProfileDefinition
            {
                Id = value.Id,
                ActivationRange = value.ActivationRange,
                DormancyRange = value.DormancyRange,
                ActiveTickRateHz = value.ActiveTickRateHz,
                DormantTickRateHz = value.DormantTickRateHz
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                Float(compiled.ActivationRange),
                Float(compiled.DormancyRange),
                Float(compiled.ActiveTickRateHz),
                Float(compiled.DormantTickRateHz));
            return compiled;
        }

        private static WorldActorRespawnProfileDefinition CompileRespawnProfile(
            WorldActorRespawnProfileAuthoringEntry value)
        {
            var compiled = new WorldActorRespawnProfileDefinition
            {
                Id = value.Id,
                DelaySeconds = value.DelaySeconds,
                PopulationLimit = value.PopulationLimit
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                Float(compiled.DelaySeconds),
                compiled.PopulationLimit.ToString(CultureInfo.InvariantCulture));
            return compiled;
        }

        private static WorldActorDefinition CompileActor(WorldActorAuthoringEntry value)
        {
            var capabilities = value.Capabilities
                .Select(capability =>
                {
                    var compiledCapability = new WorldActorCapabilityDefinition
                    {
                        Id = capability.Id,
                        Kind = capability.Kind,
                        DisplayName = capability.DisplayName
                    };
                    compiledCapability.StructuralFingerprint = Fingerprint(
                        compiledCapability.Id,
                        compiledCapability.Kind,
                        compiledCapability.DisplayName);
                    return compiledCapability;
                })
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray();
            var bounds = new WorldActorBoundsDefinition
            {
                CenterX = value.InteractionBounds.CenterX,
                CenterY = value.InteractionBounds.CenterY,
                CenterZ = value.InteractionBounds.CenterZ,
                SizeX = value.InteractionBounds.SizeX,
                SizeY = value.InteractionBounds.SizeY,
                SizeZ = value.InteractionBounds.SizeZ
            };
            var compiled = new WorldActorDefinition
            {
                Id = value.Id,
                DisplayName = value.DisplayName,
                Kind = value.Kind,
                FactionId = value.FactionId,
                Disposition = value.Disposition,
                PresentationArchetypeId = value.PresentationArchetypeId,
                DamagePolicy = value.DamagePolicy,
                Tags = value.Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(),
                InteractionBounds = bounds,
                Capabilities = capabilities,
                ActivityProfileId = value.ActivityProfileId ?? string.Empty,
                RespawnProfileId = value.RespawnProfileId ?? string.Empty
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                compiled.DisplayName,
                compiled.Kind,
                compiled.FactionId,
                compiled.Disposition,
                compiled.PresentationArchetypeId,
                compiled.DamagePolicy,
                string.Join(",", compiled.Tags),
                Float(bounds.CenterX),
                Float(bounds.CenterY),
                Float(bounds.CenterZ),
                Float(bounds.SizeX),
                Float(bounds.SizeY),
                Float(bounds.SizeZ),
                compiled.ActivityProfileId,
                compiled.RespawnProfileId,
                string.Join(",", capabilities.Select(capability => capability.StructuralFingerprint)));
            return compiled;
        }

        private static WorldActorPatrolPathDefinition CompilePatrolPath(
            WorldActorPatrolPathAuthoringEntry value)
        {
            var points = value.Points.Select(point => new WorldActorPatrolPointDefinition
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                WaitSeconds = point.WaitSeconds
            }).ToArray();
            var compiled = new WorldActorPatrolPathDefinition
            {
                Id = value.Id,
                Points = points
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                string.Join(",", points.Select(point => string.Join(
                    ":",
                    Float(point.X),
                    Float(point.Y),
                    Float(point.Z),
                    Float(point.WaitSeconds)))));
            return compiled;
        }

        private static void CompilePoint(
            WorldActorSpawnPointAuthoringEntry point,
            ICollection<WorldActorSpawnSourceDefinition> sources,
            ICollection<WorldActorSpawnInstanceDefinition> instances)
        {
            var source = CreateSpawnSource(
                point.Id,
                WorldActorSpawnKindIds.Point,
                point.ActorId,
                1,
                point.PatrolPathId,
                point.X,
                point.Y,
                point.Z,
                point.X,
                point.Z,
                point.X,
                point.Z,
                point.YawDegrees,
                1,
                1,
                0f,
                0);
            sources.Add(source);
            instances.Add(CreateSpawnInstance(
                point.Id + ":0000",
                point.Id,
                point.ActorId,
                point.X,
                point.Y,
                point.Z,
                point.YawDegrees,
                point.PatrolPathId));
        }

        private static void CompileGroup(
            WorldActorSpawnGroupAuthoringEntry group,
            ICollection<WorldActorSpawnSourceDefinition> sources,
            ICollection<WorldActorSpawnInstanceDefinition> instances)
        {
            var count = group.Rows * group.Columns;
            var halfWidth = (group.Columns - 1) * group.Spacing * 0.5f;
            var halfDepth = (group.Rows - 1) * group.Spacing * 0.5f;
            sources.Add(CreateSpawnSource(
                group.Id,
                WorldActorSpawnKindIds.Group,
                group.ActorId,
                count,
                group.PatrolPathId,
                group.CenterX,
                group.CenterY,
                group.CenterZ,
                group.CenterX - halfWidth,
                group.CenterZ - halfDepth,
                group.CenterX + halfWidth,
                group.CenterZ + halfDepth,
                group.YawDegrees,
                group.Rows,
                group.Columns,
                group.Spacing,
                0));

            var instanceIndex = 0;
            for (var row = 0; row < group.Rows; row++)
            {
                for (var column = 0; column < group.Columns; column++)
                {
                    instances.Add(CreateSpawnInstance(
                        group.Id + ":" + instanceIndex.ToString("D4", CultureInfo.InvariantCulture),
                        group.Id,
                        group.ActorId,
                        group.CenterX + (column * group.Spacing) - halfWidth,
                        group.CenterY,
                        group.CenterZ + (row * group.Spacing) - halfDepth,
                        group.YawDegrees,
                        group.PatrolPathId));
                    instanceIndex++;
                }
            }
        }

        private static void CompileArea(
            WorldActorSpawnAreaAuthoringEntry area,
            ICollection<WorldActorSpawnSourceDefinition> sources,
            ICollection<WorldActorSpawnInstanceDefinition> instances)
        {
            sources.Add(CreateSpawnSource(
                area.Id,
                WorldActorSpawnKindIds.Area,
                area.ActorId,
                area.Count,
                area.PatrolPathId,
                (area.MinimumX + area.MaximumX) * 0.5f,
                area.Y,
                (area.MinimumZ + area.MaximumZ) * 0.5f,
                area.MinimumX,
                area.MinimumZ,
                area.MaximumX,
                area.MaximumZ,
                area.YawDegrees,
                0,
                0,
                0f,
                area.Seed));
            for (var index = 0; index < area.Count; index++)
            {
                var unitX = DeterministicUnit(area.Id, area.Seed, index, "x");
                var unitZ = DeterministicUnit(area.Id, area.Seed, index, "z");
                instances.Add(CreateSpawnInstance(
                    area.Id + ":" + index.ToString("D4", CultureInfo.InvariantCulture),
                    area.Id,
                    area.ActorId,
                    area.MinimumX + ((area.MaximumX - area.MinimumX) * unitX),
                    area.Y,
                    area.MinimumZ + ((area.MaximumZ - area.MinimumZ) * unitZ),
                    area.YawDegrees,
                    area.PatrolPathId));
            }
        }

        private static WorldActorSpawnSourceDefinition CreateSpawnSource(
            string id,
            string kind,
            string actorId,
            int count,
            string patrolPathId,
            float originX,
            float originY,
            float originZ,
            float minimumX,
            float minimumZ,
            float maximumX,
            float maximumZ,
            float yaw,
            int rows,
            int columns,
            float spacing,
            int seed)
        {
            var compiled = new WorldActorSpawnSourceDefinition
            {
                Id = id,
                Kind = kind,
                ActorId = actorId,
                InstanceCount = count,
                PatrolPathId = patrolPathId ?? string.Empty,
                OriginX = originX,
                OriginY = originY,
                OriginZ = originZ,
                MinimumX = minimumX,
                MinimumZ = minimumZ,
                MaximumX = maximumX,
                MaximumZ = maximumZ,
                YawDegrees = yaw,
                Rows = rows,
                Columns = columns,
                Spacing = spacing,
                Seed = seed
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                compiled.Kind,
                compiled.ActorId,
                compiled.InstanceCount.ToString(CultureInfo.InvariantCulture),
                compiled.PatrolPathId,
                Float(compiled.OriginX),
                Float(compiled.OriginY),
                Float(compiled.OriginZ),
                Float(compiled.MinimumX),
                Float(compiled.MinimumZ),
                Float(compiled.MaximumX),
                Float(compiled.MaximumZ),
                Float(compiled.YawDegrees),
                compiled.Rows.ToString(CultureInfo.InvariantCulture),
                compiled.Columns.ToString(CultureInfo.InvariantCulture),
                Float(compiled.Spacing),
                compiled.Seed.ToString(CultureInfo.InvariantCulture));
            return compiled;
        }

        private static WorldActorSpawnInstanceDefinition CreateSpawnInstance(
            string id,
            string spawnId,
            string actorId,
            float x,
            float y,
            float z,
            float yaw,
            string patrolPathId)
        {
            var compiled = new WorldActorSpawnInstanceDefinition
            {
                Id = id,
                SpawnDefinitionId = spawnId,
                ActorId = actorId,
                X = x,
                Y = y,
                Z = z,
                YawDegrees = yaw,
                PatrolPathId = patrolPathId ?? string.Empty
            };
            compiled.StructuralFingerprint = Fingerprint(
                compiled.Id,
                compiled.SpawnDefinitionId,
                compiled.ActorId,
                Float(compiled.X),
                Float(compiled.Y),
                Float(compiled.Z),
                Float(compiled.YawDegrees),
                compiled.PatrolPathId);
            return compiled;
        }

        private static string ComputeRuntimeRevision(WorldActorRuntimeDocument runtime)
        {
            return Fingerprint(
                runtime.FormatVersion.ToString(CultureInfo.InvariantCulture),
                runtime.CatalogId,
                runtime.WorldId,
                Join(runtime.Factions.Select(value => value.StructuralFingerprint)),
                Join(runtime.PresentationArchetypes.Select(value => value.StructuralFingerprint)),
                Join(runtime.ActivityProfiles.Select(value => value.StructuralFingerprint)),
                Join(runtime.RespawnProfiles.Select(value => value.StructuralFingerprint)),
                Join(runtime.Actors.Select(value => value.StructuralFingerprint)),
                Join(runtime.SpawnSources.Select(value => value.StructuralFingerprint)),
                Join(runtime.SpawnInstances.Select(value => value.StructuralFingerprint)),
                Join(runtime.PatrolPaths.Select(value => value.StructuralFingerprint)));
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(",", values);
        }

        private static float DeterministicUnit(
            string id,
            int seed,
            int index,
            string axis)
        {
            var input = string.Join(
                "|",
                id,
                seed.ToString(CultureInfo.InvariantCulture),
                index.ToString(CultureInfo.InvariantCulture),
                axis);
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            }

            var value = (uint)hash[0]
                | ((uint)hash[1] << 8)
                | ((uint)hash[2] << 16)
                | ((uint)hash[3] << 24);
            return value / (float)uint.MaxValue;
        }

        private static HashSet<string> RequireCollection<T>(
            T[] values,
            string name,
            ICollection<string> errors)
        {
            if (values == null || values.Length == 0)
            {
                errors.Add(name + " must contain at least one entry.");
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }

        private static void AddIdentifier(
            ISet<string> ids,
            string value,
            string path,
            ICollection<string> errors)
        {
            ValidateIdentifier(value, path, errors);
            if (!string.IsNullOrWhiteSpace(value) && !ids.Add(value))
            {
                errors.Add(path + " duplicates id '" + value + "'.");
            }
        }

        private static void ValidateReference(
            string value,
            IEnumerable<string> allowed,
            string path,
            ICollection<string> errors)
        {
            ValidateIdentifier(value, path, errors);
            if (!string.IsNullOrWhiteSpace(value)
                && !allowed.Contains(value, StringComparer.Ordinal))
            {
                errors.Add(path + " references missing id '" + value + "'.");
            }
        }

        private static void ValidateSupported(
            string value,
            ISet<string> allowed,
            string path,
            ICollection<string> errors)
        {
            ValidateIdentifier(value, path, errors);
            if (!string.IsNullOrWhiteSpace(value) && !allowed.Contains(value))
            {
                errors.Add(path + " has unsupported value '" + value + "'.");
            }
        }

        private static void ValidateIdentifier(
            string value,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(path + " is required.");
                return;
            }

            if (value.Length > MaximumIdentifierLength || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                errors.Add(path + " must be a trimmed identifier no longer than " + MaximumIdentifierLength + " characters.");
                return;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if ((character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '_'
                    || character == '-')
                {
                    continue;
                }

                errors.Add(path + " must contain only lower-case letters, numbers, '.', '_', or '-'.");
                return;
            }
        }

        private static void ValidateDisplayName(
            string value,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MaximumDisplayNameLength
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                errors.Add(path + " must be trimmed and between 1 and "
                    + MaximumDisplayNameLength + " characters.");
            }
        }

        private static void ValidateOptionalEmpty(
            string value,
            string path,
            ICollection<string> errors)
        {
            if (!string.IsNullOrEmpty(value))
            {
                errors.Add(path + " must be empty for an NPC.");
            }
        }

        private static void ValidateTransform(
            float x,
            float y,
            float z,
            string path,
            ICollection<string> errors)
        {
            ValidateCoordinate(x, path + ".x", errors);
            ValidateCoordinate(y, path + ".y", errors);
            ValidateCoordinate(z, path + ".z", errors);
        }

        private static void ValidateYaw(float yaw, string path, ICollection<string> errors)
        {
            if (!IsFinite(yaw) || yaw < -360f || yaw > 360f)
            {
                errors.Add(path + " must be finite and between -360 and 360 degrees.");
            }
        }

        private static void ValidateFinite(float value, string path, ICollection<string> errors)
        {
            if (!IsFinite(value))
            {
                errors.Add(path + " must be finite.");
            }
        }

        private static void ValidateCoordinate(
            float value,
            string path,
            ICollection<string> errors)
        {
            if (!IsSupportedCoordinate(value))
            {
                errors.Add(path + " must be finite and within the supported World bounds.");
            }
        }

        private static void ValidatePositiveFinite(
            float value,
            string path,
            ICollection<string> errors)
        {
            if (!IsFinite(value) || value <= 0f)
            {
                errors.Add(path + " must be finite and positive.");
            }
        }

        private static void ValidatePositiveFinite(
            float value,
            float maximum,
            string path,
            ICollection<string> errors)
        {
            if (!IsFinite(value) || value <= 0f || value > maximum)
            {
                errors.Add(path + " must be finite, positive, and within the supported bound.");
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsSupportedCoordinate(float value)
        {
            return IsFinite(value)
                && value >= -MaximumWorldCoordinateMagnitude
                && value <= MaximumWorldCoordinateMagnitude;
        }

        private static string Float(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Fingerprint(params string[] values)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", values));
            return ComputeSha256Hex(bytes);
        }

        private static string ToHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            for (var index = 0; index < value.Length; index++)
            {
                builder.Append(value[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
