using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShooterMmo.WorldData.Actors
{
    public static partial class WorldActorCompiler
    {
        public static void ValidateRuntime(WorldActorRuntimeDocument runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            var errors = new List<string>();
            if (runtime.FormatVersion != WorldActorDataFormat.Version)
            {
                errors.Add("runtime formatVersion is unsupported.");
            }

            ValidateIdentifier(runtime.CatalogId, "runtime.catalogId", errors);
            ValidateIdentifier(runtime.WorldId, "runtime.worldId", errors);
            if (!HasRuntimeCollections(runtime, errors))
            {
                throw new WorldActorValidationException(errors);
            }

            var factionIds = ValidateOrderedRuntimeIds(
                runtime.Factions.Select(value => value.Id).ToArray(),
                "runtime.factions",
                errors);
            ValidateOrderedRuntimeIds(
                runtime.PresentationArchetypes.Select(value => value.Id).ToArray(),
                "runtime.presentationArchetypes",
                errors);
            var presentationKinds = ToRuntimeMap(
                runtime.PresentationArchetypes,
                value => value.Id,
                value => value.ActorKind);
            var activityIds = ValidateOrderedRuntimeIds(
                runtime.ActivityProfiles.Select(value => value.Id).ToArray(),
                "runtime.activityProfiles",
                errors);
            var respawnIds = ValidateOrderedRuntimeIds(
                runtime.RespawnProfiles.Select(value => value.Id).ToArray(),
                "runtime.respawnProfiles",
                errors);
            var actorIds = ValidateOrderedRuntimeIds(
                runtime.Actors.Select(value => value.Id).ToArray(),
                "runtime.actors",
                errors);
            var actorKinds = ToRuntimeMap(
                runtime.Actors,
                value => value.Id,
                value => value.Kind);
            var sourceIds = ValidateOrderedRuntimeIds(
                runtime.SpawnSources.Select(value => value.Id).ToArray(),
                "runtime.spawnSources",
                errors);
            var sourcesById = ToRuntimeMap(
                runtime.SpawnSources,
                value => value.Id,
                value => value);
            var patrolIds = ValidateOrderedRuntimeIds(
                runtime.PatrolPaths.Select(value => value.Id).ToArray(),
                "runtime.patrolPaths",
                errors);
            ValidateOrderedRuntimeIds(
                runtime.SpawnInstances.Select(value => value.Id).ToArray(),
                "runtime.spawnInstances",
                errors,
                true);

            ValidateRuntimeFactions(runtime.Factions, errors);
            ValidateRuntimePresentations(runtime.PresentationArchetypes, errors);
            ValidateRuntimeActivityProfiles(runtime.ActivityProfiles, errors);
            ValidateRuntimeRespawnProfiles(runtime.RespawnProfiles, errors);
            ValidateRuntimeActors(
                runtime.Actors,
                factionIds,
                presentationKinds,
                activityIds,
                respawnIds,
                errors);
            ValidateRuntimePatrols(runtime.PatrolPaths, errors);
            ValidateRuntimeSources(
                runtime.SpawnSources,
                actorIds,
                actorKinds,
                patrolIds,
                errors);
            ValidateRuntimeInstances(
                runtime.SpawnInstances,
                actorIds,
                sourceIds,
                sourcesById,
                patrolIds,
                errors);

            if (!string.Equals(
                    runtime.Revision,
                    ComputeRuntimeRevision(runtime),
                    StringComparison.Ordinal))
            {
                errors.Add("runtime revision does not match its structural fingerprints.");
            }

            if (errors.Count > 0)
            {
                throw new WorldActorValidationException(errors);
            }
        }

        private static bool HasRuntimeCollections(
            WorldActorRuntimeDocument runtime,
            ICollection<string> errors)
        {
            var valid = true;
            valid &= RequireRuntimeCollection(runtime.Factions, "runtime.factions", errors, true);
            valid &= RequireRuntimeCollection(
                runtime.PresentationArchetypes,
                "runtime.presentationArchetypes",
                errors,
                true);
            valid &= RequireRuntimeCollection(
                runtime.ActivityProfiles,
                "runtime.activityProfiles",
                errors,
                true);
            valid &= RequireRuntimeCollection(
                runtime.RespawnProfiles,
                "runtime.respawnProfiles",
                errors,
                true);
            valid &= RequireRuntimeCollection(runtime.Actors, "runtime.actors", errors, true);
            valid &= RequireRuntimeCollection(
                runtime.SpawnSources,
                "runtime.spawnSources",
                errors,
                true);
            valid &= RequireRuntimeCollection(
                runtime.SpawnInstances,
                "runtime.spawnInstances",
                errors,
                true);
            valid &= RequireRuntimeCollection(
                runtime.PatrolPaths,
                "runtime.patrolPaths",
                errors,
                false);
            if (runtime.Factions != null
                && runtime.Factions.Length > MaximumDefinitionsPerCollection
                || runtime.PresentationArchetypes != null
                && runtime.PresentationArchetypes.Length > MaximumDefinitionsPerCollection
                || runtime.ActivityProfiles != null
                && runtime.ActivityProfiles.Length > MaximumDefinitionsPerCollection
                || runtime.RespawnProfiles != null
                && runtime.RespawnProfiles.Length > MaximumDefinitionsPerCollection
                || runtime.Actors != null
                && runtime.Actors.Length > MaximumDefinitionsPerCollection)
            {
                errors.Add("runtime definition collections exceed the supported bound.");
                valid = false;
            }

            if (runtime.SpawnSources != null
                && runtime.SpawnSources.Length > MaximumSpawnSources
                || runtime.SpawnInstances != null
                && runtime.SpawnInstances.Length > MaximumRuntimeInstances
                || runtime.PatrolPaths != null
                && runtime.PatrolPaths.Length > MaximumSpawnSources)
            {
                errors.Add("runtime actor population exceeds the supported bound.");
                valid = false;
            }
            return valid;
        }

        private static bool RequireRuntimeCollection<T>(
            T[] values,
            string path,
            ICollection<string> errors,
            bool requireEntries)
        {
            if (values == null || (requireEntries && values.Length == 0))
            {
                errors.Add(path + (requireEntries
                    ? " must contain at least one entry."
                    : " is required."));
                return false;
            }

            if (values.Any(value => ReferenceEquals(value, null)))
            {
                errors.Add(path + " must not contain null entries.");
                return false;
            }

            return true;
        }

        private static HashSet<string> ValidateOrderedRuntimeIds(
            string[] values,
            string path,
            ICollection<string> errors,
            bool allowInstanceSeparator = false)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                if (allowInstanceSeparator)
                {
                    ValidateRuntimeInstanceId(
                        ids,
                        values[index],
                        path + "[" + index + "].id",
                        errors);
                }
                else
                {
                    AddIdentifier(ids, values[index], path + "[" + index + "].id", errors);
                }
                if (index > 0
                    && string.CompareOrdinal(values[index - 1], values[index]) >= 0)
                {
                    errors.Add(path + " must be strictly ordered by id.");
                }
            }

            return ids;
        }

        private static Dictionary<string, TValue> ToRuntimeMap<TEntry, TValue>(
            IEnumerable<TEntry> values,
            Func<TEntry, string> idSelector,
            Func<TEntry, TValue> valueSelector)
        {
            var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var id = idSelector(value);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.TryAdd(id, valueSelector(value));
                }
            }

            return result;
        }

        private static void ValidateRuntimeInstanceId(
            ISet<string> ids,
            string value,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MaximumIdentifierLength + 5
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Count(character => character == ':') != 1
                || value.Any(character => !(
                    (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '_'
                    || character == '-'
                    || character == ':')))
            {
                errors.Add(path + " is not a valid compiled runtime instance id.");
                return;
            }

            if (!ids.Add(value))
            {
                errors.Add(path + " duplicates runtime instance id '" + value + "'.");
            }
        }

        private static void ValidateRuntimeFactions(
            WorldActorFactionDefinition[] values,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                ValidateDisplayName(value.DisplayName, "runtime faction displayName", errors);
                ValidateSupported(
                    value.DefaultDisposition,
                    SupportedDispositions,
                    "runtime faction defaultDisposition",
                    errors);
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    Fingerprint(value.Id, value.DisplayName, value.DefaultDisposition),
                    "runtime faction " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimePresentations(
            WorldActorPresentationDefinition[] values,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                ValidateDisplayName(value.DisplayName, "runtime presentation displayName", errors);
                ValidateSupported(
                    value.ActorKind,
                    SupportedActorKinds,
                    "runtime presentation actorKind",
                    errors);
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    Fingerprint(value.Id, value.DisplayName, value.ActorKind),
                    "runtime presentation " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimeActivityProfiles(
            WorldActorActivityProfileDefinition[] values,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                if (!IsFinite(value.ActivationRange)
                    || !IsFinite(value.DormancyRange)
                    || value.ActivationRange <= 0f
                    || value.DormancyRange <= value.ActivationRange
                    || !IsFinite(value.ActiveTickRateHz)
                    || !IsFinite(value.DormantTickRateHz)
                    || value.ActiveTickRateHz <= 0f
                    || value.DormantTickRateHz <= 0f
                    || value.ActivationRange > MaximumActivityRange
                    || value.DormancyRange > MaximumActivityRange
                    || value.ActiveTickRateHz > MaximumActiveTickRateHz
                    || value.DormantTickRateHz > MaximumDormantTickRateHz
                    || value.DormantTickRateHz > value.ActiveTickRateHz)
                {
                    errors.Add("runtime activity profile " + value.Id + " is invalid.");
                }

                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    Fingerprint(
                        value.Id,
                        Float(value.ActivationRange),
                        Float(value.DormancyRange),
                        Float(value.ActiveTickRateHz),
                        Float(value.DormantTickRateHz)),
                    "runtime activity profile " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimeRespawnProfiles(
            WorldActorRespawnProfileDefinition[] values,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                if (!IsFinite(value.DelaySeconds)
                    || value.DelaySeconds < 0f
                    || value.PopulationLimit < 1
                    || value.PopulationLimit > MaximumInstancesPerSource)
                {
                    errors.Add("runtime respawn profile " + value.Id + " is invalid.");
                }

                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    Fingerprint(
                        value.Id,
                        Float(value.DelaySeconds),
                        value.PopulationLimit.ToString(CultureInfo.InvariantCulture)),
                    "runtime respawn profile " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimeActors(
            WorldActorDefinition[] values,
            ISet<string> factionIds,
            IReadOnlyDictionary<string, string> presentationKinds,
            ISet<string> activityIds,
            ISet<string> respawnIds,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                ValidateDisplayName(value.DisplayName, "runtime actor displayName", errors);
                ValidateSupported(value.Kind, SupportedActorKinds, "runtime actor kind", errors);
                ValidateReference(value.FactionId, factionIds, "runtime actor factionId", errors);
                ValidateSupported(
                    value.Disposition,
                    SupportedDispositions,
                    "runtime actor disposition",
                    errors);
                ValidateReference(
                    value.PresentationArchetypeId,
                    presentationKinds.Keys,
                    "runtime actor presentationArchetypeId",
                    errors);
                if (presentationKinds.TryGetValue(
                        value.PresentationArchetypeId ?? string.Empty,
                        out var presentationKind)
                    && !string.Equals(presentationKind, value.Kind, StringComparison.Ordinal))
                {
                    errors.Add("runtime actor " + value.Id
                        + " uses a presentation for another actor kind.");
                }
                ValidateSupported(
                    value.DamagePolicy,
                    SupportedDamagePolicies,
                    "runtime actor damagePolicy",
                    errors);
                if (value.Kind == WorldActorKindIds.Npc
                    && value.DamagePolicy != WorldActorDamagePolicyIds.Invulnerable)
                {
                    errors.Add("runtime NPC " + value.Id + " must be invulnerable.");
                }

                if (value.Kind == WorldActorKindIds.Mob)
                {
                    ValidateReference(
                        value.ActivityProfileId,
                        activityIds,
                        "runtime actor activityProfileId",
                        errors);
                    ValidateReference(
                        value.RespawnProfileId,
                        respawnIds,
                        "runtime actor respawnProfileId",
                        errors);
                }
                else
                {
                    ValidateOptionalEmpty(
                        value.ActivityProfileId,
                        "runtime actor activityProfileId",
                        errors);
                    ValidateOptionalEmpty(
                        value.RespawnProfileId,
                        "runtime actor respawnProfileId",
                        errors);
                }

                if (value.InteractionBounds == null
                    || !IsSupportedCoordinate(value.InteractionBounds.CenterX)
                    || !IsSupportedCoordinate(value.InteractionBounds.CenterY)
                    || !IsSupportedCoordinate(value.InteractionBounds.CenterZ)
                    || !IsFinite(value.InteractionBounds.SizeX)
                    || !IsFinite(value.InteractionBounds.SizeY)
                    || !IsFinite(value.InteractionBounds.SizeZ)
                    || value.InteractionBounds.SizeX <= 0f
                    || value.InteractionBounds.SizeY <= 0f
                    || value.InteractionBounds.SizeZ <= 0f
                    || value.InteractionBounds.SizeX > MaximumInteractionBoundsSize
                    || value.InteractionBounds.SizeY > MaximumInteractionBoundsSize
                    || value.InteractionBounds.SizeZ > MaximumInteractionBoundsSize)
                {
                    errors.Add("runtime actor " + value.Id + " has invalid bounds.");
                    continue;
                }

                if (value.Tags == null
                    || value.Capabilities == null
                    || value.Tags.Length > MaximumTagsPerActor
                    || value.Capabilities.Length > MaximumCapabilitiesPerActor)
                {
                    errors.Add("runtime actor " + value.Id + " has invalid tags or capabilities.");
                    continue;
                }

                var tagIds = new HashSet<string>(StringComparer.Ordinal);
                for (var tagIndex = 0; tagIndex < value.Tags.Length; tagIndex++)
                {
                    AddIdentifier(
                        tagIds,
                        value.Tags[tagIndex],
                        "runtime actor tag",
                        errors);
                    if (tagIndex > 0
                        && string.CompareOrdinal(
                            value.Tags[tagIndex - 1],
                            value.Tags[tagIndex]) >= 0)
                    {
                        errors.Add("runtime actor " + value.Id
                            + " tags must be strictly ordered.");
                    }
                }

                if (value.Kind == WorldActorKindIds.Mob
                    && value.Capabilities.Length > 0)
                {
                    errors.Add("runtime Mob " + value.Id
                        + " cannot expose Phase 12 NPC capabilities.");
                }

                var capabilityFingerprints = new List<string>();
                var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
                for (var capabilityIndex = 0;
                     capabilityIndex < value.Capabilities.Length;
                     capabilityIndex++)
                {
                    var capability = value.Capabilities[capabilityIndex];
                    if (capability == null)
                    {
                        errors.Add("runtime actor " + value.Id + " has a null capability.");
                        continue;
                    }

                    AddIdentifier(
                        capabilityIds,
                        capability.Id,
                        "runtime capability id",
                        errors);
                    ValidateDisplayName(
                        capability.DisplayName,
                        "runtime capability displayName",
                        errors);
                    if (capabilityIndex > 0
                        && value.Capabilities[capabilityIndex - 1] != null
                        && string.CompareOrdinal(
                            value.Capabilities[capabilityIndex - 1].Id,
                            capability.Id) >= 0)
                    {
                        errors.Add("runtime actor " + value.Id
                            + " capabilities must be strictly ordered.");
                    }

                    ValidateSupported(
                        capability.Kind,
                        SupportedCapabilityKinds,
                        "runtime capability kind",
                        errors);
                    var expected = Fingerprint(
                        capability.Id,
                        capability.Kind,
                        capability.DisplayName);
                    CheckRuntimeFingerprint(
                        capability.StructuralFingerprint,
                        expected,
                        "runtime capability " + capability.Id,
                        errors);
                    capabilityFingerprints.Add(expected);
                }

                var bounds = value.InteractionBounds;
                var actorFingerprint = Fingerprint(
                    value.Id,
                    value.DisplayName,
                    value.Kind,
                    value.FactionId,
                    value.Disposition,
                    value.PresentationArchetypeId,
                    value.DamagePolicy,
                    string.Join(",", value.Tags),
                    Float(bounds.CenterX),
                    Float(bounds.CenterY),
                    Float(bounds.CenterZ),
                    Float(bounds.SizeX),
                    Float(bounds.SizeY),
                    Float(bounds.SizeZ),
                    value.ActivityProfileId,
                    value.RespawnProfileId,
                    string.Join(",", capabilityFingerprints));
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    actorFingerprint,
                    "runtime actor " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimePatrols(
            WorldActorPatrolPathDefinition[] values,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                if (value.Points == null || value.Points.Length < 2)
                {
                    errors.Add("runtime patrol " + value.Id + " must contain two points.");
                    continue;
                }

                if (value.Points.Length > MaximumPatrolPoints)
                {
                    errors.Add("runtime patrol " + value.Id
                        + " points exceed the supported bound.");
                }

                var compiledPoints = new List<string>();
                for (var pointIndex = 0; pointIndex < value.Points.Length; pointIndex++)
                {
                    var point = value.Points[pointIndex];
                    if (point == null)
                    {
                        errors.Add("runtime patrol " + value.Id
                            + " contains a null point.");
                        continue;
                    }

                    ValidateTransform(
                        point.X,
                        point.Y,
                        point.Z,
                        "runtime patrol point",
                        errors);
                    if (!IsFinite(point.WaitSeconds) || point.WaitSeconds < 0f)
                    {
                        errors.Add("runtime patrol " + value.Id
                            + " has an invalid wait duration.");
                    }

                    compiledPoints.Add(string.Join(
                        ":",
                        Float(point.X),
                        Float(point.Y),
                        Float(point.Z),
                        Float(point.WaitSeconds)));
                }

                var expected = Fingerprint(
                    value.Id,
                    string.Join(",", compiledPoints));
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    expected,
                    "runtime patrol " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimeSources(
            WorldActorSpawnSourceDefinition[] values,
            ISet<string> actorIds,
            IReadOnlyDictionary<string, string> actorKinds,
            ISet<string> patrolIds,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                ValidateSupported(
                    value.Kind,
                    SupportedSpawnKinds,
                    "runtime spawn kind",
                    errors);
                ValidateReference(value.ActorId, actorIds, "runtime spawn actorId", errors);
                if (!string.IsNullOrEmpty(value.PatrolPathId))
                {
                    ValidateReference(
                        value.PatrolPathId,
                        patrolIds,
                        "runtime spawn patrolPathId",
                        errors);
                    if (actorKinds.TryGetValue(value.ActorId ?? string.Empty, out var actorKind)
                        && actorKind != WorldActorKindIds.Mob)
                    {
                        errors.Add("runtime spawn " + value.Id
                            + " assigns a patrol to a non-Mob actor.");
                    }
                }

                ValidateTransform(
                    value.OriginX,
                    value.OriginY,
                    value.OriginZ,
                    "runtime spawn origin",
                    errors);
                ValidateCoordinate(value.MinimumX, "runtime spawn minimumX", errors);
                ValidateCoordinate(value.MinimumZ, "runtime spawn minimumZ", errors);
                ValidateCoordinate(value.MaximumX, "runtime spawn maximumX", errors);
                ValidateCoordinate(value.MaximumZ, "runtime spawn maximumZ", errors);
                ValidateYaw(value.YawDegrees, "runtime spawn yawDegrees", errors);
                if (value.InstanceCount < 1
                    || value.InstanceCount > MaximumInstancesPerSource)
                {
                    errors.Add("runtime spawn " + value.Id
                        + " has an invalid instance count.");
                }

                if (value.Kind == WorldActorSpawnKindIds.Point
                    && (value.InstanceCount != 1
                        || value.Rows != 1
                        || value.Columns != 1
                        || value.Spacing != 0f
                        || value.Seed != 0
                        || value.MinimumX != value.OriginX
                        || value.MaximumX != value.OriginX
                        || value.MinimumZ != value.OriginZ
                        || value.MaximumZ != value.OriginZ))
                {
                    errors.Add("runtime point spawn " + value.Id + " is invalid.");
                }
                else if (value.Kind == WorldActorSpawnKindIds.Group
                    && (value.Rows < 1
                        || value.Columns < 1
                        || (long)value.Rows * value.Columns != value.InstanceCount
                        || !IsFinite(value.Spacing)
                        || value.Spacing <= 0f
                        || value.MaximumX < value.MinimumX
                        || value.MaximumZ < value.MinimumZ))
                {
                    errors.Add("runtime group spawn " + value.Id + " is invalid.");
                }
                else if (value.Kind == WorldActorSpawnKindIds.Area
                    && (value.Rows != 0
                        || value.Columns != 0
                        || value.Spacing != 0f
                        || value.MaximumX <= value.MinimumX
                        || value.MaximumZ <= value.MinimumZ))
                {
                    errors.Add("runtime area spawn " + value.Id + " is invalid.");
                }

                if (value.Kind == WorldActorSpawnKindIds.Group)
                {
                    var halfWidth = (value.Columns - 1) * value.Spacing * 0.5f;
                    var halfDepth = (value.Rows - 1) * value.Spacing * 0.5f;
                    if (value.Seed != 0
                        || value.MinimumX != value.OriginX - halfWidth
                        || value.MaximumX != value.OriginX + halfWidth
                        || value.MinimumZ != value.OriginZ - halfDepth
                        || value.MaximumZ != value.OriginZ + halfDepth)
                    {
                        errors.Add("runtime group spawn " + value.Id
                            + " bounds do not match its grid.");
                    }
                }
                else if (value.Kind == WorldActorSpawnKindIds.Area
                    && (value.OriginX != (value.MinimumX + value.MaximumX) * 0.5f
                        || value.OriginZ != (value.MinimumZ + value.MaximumZ) * 0.5f))
                {
                    errors.Add("runtime area spawn " + value.Id
                        + " origin does not match its bounds.");
                }

                var expected = Fingerprint(
                    value.Id,
                    value.Kind,
                    value.ActorId,
                    value.InstanceCount.ToString(CultureInfo.InvariantCulture),
                    value.PatrolPathId,
                    Float(value.OriginX),
                    Float(value.OriginY),
                    Float(value.OriginZ),
                    Float(value.MinimumX),
                    Float(value.MinimumZ),
                    Float(value.MaximumX),
                    Float(value.MaximumZ),
                    Float(value.YawDegrees),
                    value.Rows.ToString(CultureInfo.InvariantCulture),
                    value.Columns.ToString(CultureInfo.InvariantCulture),
                    Float(value.Spacing),
                    value.Seed.ToString(CultureInfo.InvariantCulture));
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    expected,
                    "runtime spawn source " + value.Id,
                    errors);
            }
        }

        private static void ValidateRuntimeInstances(
            WorldActorSpawnInstanceDefinition[] values,
            ISet<string> actorIds,
            ISet<string> sourceIds,
            IReadOnlyDictionary<string, WorldActorSpawnSourceDefinition> sourcesById,
            ISet<string> patrolIds,
            ICollection<string> errors)
        {
            foreach (var value in values)
            {
                ValidateReference(value.ActorId, actorIds, "runtime instance actorId", errors);
                ValidateReference(
                    value.SpawnDefinitionId,
                    sourceIds,
                    "runtime instance spawnDefinitionId",
                    errors);
                if (!string.IsNullOrEmpty(value.PatrolPathId))
                {
                    ValidateReference(
                        value.PatrolPathId,
                        patrolIds,
                        "runtime instance patrolPathId",
                        errors);
                }

                ValidateTransform(
                    value.X,
                    value.Y,
                    value.Z,
                    "runtime spawn instance",
                    errors);
                ValidateYaw(
                    value.YawDegrees,
                    "runtime spawn instance yawDegrees",
                    errors);
                if (sourcesById.TryGetValue(value.SpawnDefinitionId ?? string.Empty, out var source)
                    && (!string.Equals(source.ActorId, value.ActorId, StringComparison.Ordinal)
                        || !string.Equals(
                            source.PatrolPathId,
                            value.PatrolPathId,
                            StringComparison.Ordinal)
                        || value.Id == null
                        || !value.Id.StartsWith(source.Id + ":", StringComparison.Ordinal)))
                {
                    errors.Add("runtime spawn instance " + value.Id
                        + " does not match its source.");
                }

                var expected = Fingerprint(
                    value.Id,
                    value.SpawnDefinitionId,
                    value.ActorId,
                    Float(value.X),
                    Float(value.Y),
                    Float(value.Z),
                    Float(value.YawDegrees),
                    value.PatrolPathId);
                CheckRuntimeFingerprint(
                    value.StructuralFingerprint,
                    expected,
                    "runtime spawn instance " + value.Id,
                    errors);
            }


            var instancesBySource = new Dictionary<
                string,
                List<WorldActorSpawnInstanceDefinition>>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value.SpawnDefinitionId))
                {
                    continue;
                }

                if (!instancesBySource.TryGetValue(
                        value.SpawnDefinitionId,
                        out var sourceInstances))
                {
                    sourceInstances = new List<WorldActorSpawnInstanceDefinition>();
                    instancesBySource.Add(value.SpawnDefinitionId, sourceInstances);
                }

                sourceInstances.Add(value);
            }
            foreach (var source in sourcesById.Values)
            {
                instancesBySource.TryGetValue(source.Id, out var sourceInstances);
                sourceInstances ??= new List<WorldActorSpawnInstanceDefinition>();
                sourceInstances.Sort((left, right) =>
                    string.CompareOrdinal(left.Id, right.Id));
                if (sourceInstances.Count != source.InstanceCount)
                {
                    errors.Add("runtime spawn source " + source.Id
                        + " does not match its compiled instance count.");
                    continue;
                }

                for (var index = 0; index < sourceInstances.Count; index++)
                {
                    ValidateRuntimeInstancePlacement(
                        source,
                        sourceInstances[index],
                        index,
                        errors);
                }
            }
        }

        private static void ValidateRuntimeInstancePlacement(
            WorldActorSpawnSourceDefinition source,
            WorldActorSpawnInstanceDefinition instance,
            int index,
            ICollection<string> errors)
        {
            var expectedId = source.Id + ":"
                + index.ToString("D4", CultureInfo.InvariantCulture);
            var expectedX = source.OriginX;
            var expectedY = source.OriginY;
            var expectedZ = source.OriginZ;
            if (source.Kind == WorldActorSpawnKindIds.Group)
            {
                if (source.Rows <= 0 || source.Columns <= 0)
                {
                    return;
                }

                var row = index / source.Columns;
                var column = index % source.Columns;
                var halfWidth = (source.Columns - 1) * source.Spacing * 0.5f;
                var halfDepth = (source.Rows - 1) * source.Spacing * 0.5f;
                expectedX = source.OriginX + (column * source.Spacing) - halfWidth;
                expectedZ = source.OriginZ + (row * source.Spacing) - halfDepth;
            }
            else if (source.Kind == WorldActorSpawnKindIds.Area)
            {
                expectedX = source.MinimumX
                    + ((source.MaximumX - source.MinimumX)
                        * DeterministicUnit(source.Id, source.Seed, index, "x"));
                expectedZ = source.MinimumZ
                    + ((source.MaximumZ - source.MinimumZ)
                        * DeterministicUnit(source.Id, source.Seed, index, "z"));
            }

            if (!string.Equals(instance.Id, expectedId, StringComparison.Ordinal)
                || instance.X != expectedX
                || instance.Y != expectedY
                || instance.Z != expectedZ
                || instance.YawDegrees != source.YawDegrees)
            {
                errors.Add("runtime spawn instance " + instance.Id
                    + " does not match deterministic source placement.");
            }
        }

        private static void CheckRuntimeFingerprint(
            string actual,
            string expected,
            string path,
            ICollection<string> errors)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                errors.Add(path + " structural fingerprint is invalid.");
            }
        }
    }
}
