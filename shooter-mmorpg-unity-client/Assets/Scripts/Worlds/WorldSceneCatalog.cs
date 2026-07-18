using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShooterMmo.Worlds
{
    [Serializable]
    public sealed class WorldSceneCatalogDocument
    {
        public int formatVersion;
        public WorldSceneCatalogEntry[] entries = new WorldSceneCatalogEntry[0];
    }

    [Serializable]
    public sealed class WorldSceneCatalogEntry
    {
        public string worldId = string.Empty;
        public string sceneName = string.Empty;
    }

    public static class WorldSceneCatalog
    {
        public const int FormatVersion = 1;
        public const string ResourcePath = "Worlds/world-scene-catalog";

        private static bool loadAttempted;
        private static WorldSceneCatalogDocument loadedCatalog;
        private static string loadError = string.Empty;

        public static bool TryResolveScene(
            string worldId,
            out string sceneName,
            out string error)
        {
            sceneName = string.Empty;
            if (!TryLoad(out var catalog, out error))
            {
                return false;
            }

            for (var index = 0; index < catalog.entries.Length; index++)
            {
                var entry = catalog.entries[index];
                if (string.Equals(entry.worldId, worldId, StringComparison.Ordinal))
                {
                    sceneName = entry.sceneName;
                    return true;
                }
            }

            error = "World '" + worldId + "' is not present in the client world scene catalog.";
            return false;
        }

        public static bool IsWorldScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)
                || !TryLoad(out var catalog, out _))
            {
                return false;
            }

            for (var index = 0; index < catalog.entries.Length; index++)
            {
                if (string.Equals(
                        catalog.entries[index].sceneName,
                        sceneName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryParse(
            string json,
            out WorldSceneCatalogDocument catalog,
            out string error)
        {
            catalog = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The client world scene catalog is empty.";
                return false;
            }

            try
            {
                catalog = JsonUtility.FromJson<WorldSceneCatalogDocument>(json);
            }
            catch (ArgumentException exception)
            {
                error = "The client world scene catalog JSON is invalid: " + exception.Message;
                return false;
            }

            return TryValidate(catalog, out error);
        }

        private static bool TryLoad(
            out WorldSceneCatalogDocument catalog,
            out string error)
        {
            if (!loadAttempted)
            {
                loadAttempted = true;
                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null)
                {
                    loadError = "The client world scene catalog resource is missing.";
                }
                else if (!TryParse(asset.text, out loadedCatalog, out loadError))
                {
                    loadedCatalog = null;
                }

                if (asset != null)
                {
                    Resources.UnloadAsset(asset);
                }
            }

            catalog = loadedCatalog;
            error = loadError;
            return catalog != null;
        }

        private static bool TryValidate(
            WorldSceneCatalogDocument catalog,
            out string error)
        {
            error = string.Empty;
            if (catalog == null)
            {
                error = "The client world scene catalog has no root document.";
                return false;
            }

            if (catalog.formatVersion != FormatVersion)
            {
                error = "The client world scene catalog format version is unsupported.";
                return false;
            }

            if (catalog.entries == null || catalog.entries.Length == 0)
            {
                error = "The client world scene catalog has no entries.";
                return false;
            }

            var worldIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < catalog.entries.Length; index++)
            {
                var entry = catalog.entries[index];
                if (entry == null || !IsValidWorldId(entry.worldId))
                {
                    error = "The client world scene catalog contains an invalid world id.";
                    return false;
                }

                if (!IsValidSceneName(entry.sceneName))
                {
                    error = "The client world scene catalog contains an invalid scene name.";
                    return false;
                }

                if (!worldIds.Add(entry.worldId))
                {
                    error = "The client world scene catalog contains duplicate world ids.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidWorldId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var isAsciiLetterOrDigit = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9');
                if (!isAsciiLetterOrDigit && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidSceneName(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 128
                && value.IndexOf('/') < 0
                && value.IndexOf('\\') < 0;
        }
    }
}
