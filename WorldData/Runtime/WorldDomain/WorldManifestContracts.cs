using System;
using System.Collections.Generic;

namespace ShooterMmo.WorldData.Worlds
{
    public static class WorldManifestFormat
    {
        public const int Version = 1;
        public const string FileName = "world.json";
    }

    [Serializable]
    public sealed class WorldManifestDocument
    {
        public int formatVersion;
        public string worldId = string.Empty;
        public string displayName = string.Empty;
        public string clientSceneName = string.Empty;
        public float groundHeight;
        public WorldBoundsDocument bounds = new WorldBoundsDocument();
        public WorldSpawnDocument spawn = new WorldSpawnDocument();
        public WorldServicePointDocument[] servicePoints = Array.Empty<WorldServicePointDocument>();
    }

    [Serializable]
    public sealed class WorldBoundsDocument
    {
        public float minimumX;
        public float maximumX;
        public float minimumZ;
        public float maximumZ;
    }

    [Serializable]
    public sealed class WorldSpawnDocument
    {
        public float x;
        public float y;
        public float z;
        public float yawDegrees;
    }

    [Serializable]
    public sealed class WorldServicePointDocument
    {
        public string id = string.Empty;
        public string kind = string.Empty;
        public float x;
        public float y;
        public float z;
        public float radius;
    }

    public sealed class WorldManifestValidationException : Exception
    {
        public WorldManifestValidationException(string message)
            : base(message)
        {
        }
    }

    public static class WorldManifestValidator
    {
        public static void Validate(WorldManifestDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.formatVersion != WorldManifestFormat.Version)
            {
                throw new WorldManifestValidationException(
                    "World manifest format version is unsupported.");
            }

            ValidateIdentifier(document.worldId, "WorldId");
            ValidateDisplayName(document.displayName, "DisplayName");
            ValidateDisplayName(document.clientSceneName, "ClientSceneName");
            ValidateFinite(document.groundHeight, "GroundHeight");
            if (document.bounds == null)
            {
                throw new WorldManifestValidationException("Bounds are required.");
            }

            ValidateFinite(document.bounds.minimumX, "Bounds.MinimumX");
            ValidateFinite(document.bounds.maximumX, "Bounds.MaximumX");
            ValidateFinite(document.bounds.minimumZ, "Bounds.MinimumZ");
            ValidateFinite(document.bounds.maximumZ, "Bounds.MaximumZ");
            if (document.bounds.minimumX >= document.bounds.maximumX
                || document.bounds.minimumZ >= document.bounds.maximumZ)
            {
                throw new WorldManifestValidationException(
                    "World bounds minimums must be lower than maximums.");
            }

            if (document.spawn == null)
            {
                throw new WorldManifestValidationException("Spawn is required.");
            }

            ValidatePosition(
                document.spawn.x,
                document.spawn.y,
                document.spawn.z,
                "Spawn");
            ValidateFinite(document.spawn.yawDegrees, "Spawn.YawDegrees");
            ValidateInsideBounds(document, document.spawn.x, document.spawn.z, "Spawn");

            if (document.servicePoints == null)
            {
                throw new WorldManifestValidationException("ServicePoints are required.");
            }

            var servicePointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var servicePoint in document.servicePoints)
            {
                if (servicePoint == null)
                {
                    throw new WorldManifestValidationException(
                        "ServicePoints cannot contain null entries.");
                }

                ValidateIdentifier(servicePoint.id, "ServicePoint.Id");
                ValidateIdentifier(servicePoint.kind, "ServicePoint.Kind");
                if (servicePoint.kind != "bank"
                    && servicePoint.kind != "recovery_storage"
                    && servicePoint.kind != "insurance_npc")
                {
                    throw new WorldManifestValidationException(
                        "ServicePoint kind must be bank, recovery_storage, or insurance_npc.");
                }
                if (!servicePointIds.Add(servicePoint.id))
                {
                    throw new WorldManifestValidationException(
                        "ServicePoint ids must be unique within a World.");
                }

                ValidatePosition(
                    servicePoint.x,
                    servicePoint.y,
                    servicePoint.z,
                    "ServicePoint");
                ValidateFinite(servicePoint.radius, "ServicePoint.Radius");
                if (servicePoint.radius <= 0f || servicePoint.radius > 100f)
                {
                    throw new WorldManifestValidationException(
                        "ServicePoint radius must be greater than 0 and at most 100.");
                }

                ValidateInsideBounds(
                    document,
                    servicePoint.x,
                    servicePoint.z,
                    "ServicePoint");
            }
        }

        private static void ValidateInsideBounds(
            WorldManifestDocument document,
            float x,
            float z,
            string name)
        {
            if (x < document.bounds.minimumX
                || x > document.bounds.maximumX
                || z < document.bounds.minimumZ
                || z > document.bounds.maximumZ)
            {
                throw new WorldManifestValidationException(
                    name + " must be inside the configured World bounds.");
            }
        }

        private static void ValidatePosition(float x, float y, float z, string name)
        {
            ValidateFinite(x, name + ".X");
            ValidateFinite(y, name + ".Y");
            ValidateFinite(z, name + ".Z");
        }

        private static void ValidateIdentifier(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > 128)
            {
                throw new WorldManifestValidationException(
                    name + " must be a valid identifier.");
            }

            foreach (var character in value)
            {
                if (!IsAsciiLetterOrDigit(character)
                    && character != '-'
                    && character != '_')
                {
                    throw new WorldManifestValidationException(
                        name + " must be a valid identifier.");
                }
            }
        }

        private static void ValidateDisplayName(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            {
                throw new WorldManifestValidationException(
                    name + " is required and must not exceed 128 characters.");
            }
        }

        private static void ValidateFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new WorldManifestValidationException(name + " must be finite.");
            }
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return value >= 'a' && value <= 'z'
                || value >= 'A' && value <= 'Z'
                || value >= '0' && value <= '9';
        }
    }
}
