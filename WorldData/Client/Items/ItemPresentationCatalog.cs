using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ShooterMmo.WorldData.Items.Presentation
{
    public static class ItemPresentationCatalogFormat
    {
        public const int Version = 1;
    }

    [Serializable]
    public sealed class ItemPresentationCatalogDocument
    {
        public int formatVersion;
        public string sourceCatalogRevision;
        public string presentationRevision;
        public ItemPresentationEntry[] entries;
    }

    [Serializable]
    public sealed class ItemPresentationEntry
    {
        public string definitionId;
        public string iconResourcePath;
        public string localizationKey;
        public string fallbackDisplayName;
        public string prefabPresentationKey;
    }

    public sealed class ItemPresentationCatalogValidationException : Exception
    {
        public ItemPresentationCatalogValidationException(IReadOnlyCollection<string> errors)
            : base(BuildMessage(errors))
        {
            Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        }

        public IReadOnlyCollection<string> Errors { get; }

        private static string BuildMessage(IReadOnlyCollection<string> errors)
        {
            if (errors == null || errors.Count == 0)
            {
                return "Item presentation catalog validation failed.";
            }

            return "Item presentation catalog validation failed:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => "- " + error));
        }
    }

    public static class ItemPresentationCatalogCompiler
    {
        private static readonly string[] UnsupportedResourceExtensions =
        {
            ".asset",
            ".bmp",
            ".gif",
            ".jpeg",
            ".jpg",
            ".png",
            ".psd",
            ".spriteatlas",
            ".tga",
            ".tif",
            ".tiff"
        };

        public static ItemPresentationCatalogDocument Compile(
            string sourceCatalogRevision,
            IEnumerable<string> expectedDefinitionIds,
            IEnumerable<ItemPresentationEntry> entries)
        {
            var errors = new List<string>();
            if (!IsSha256(sourceCatalogRevision))
            {
                errors.Add("sourceCatalogRevision must be a lowercase SHA-256 value.");
            }

            var expectedIds = BuildExpectedIds(expectedDefinitionIds, errors);
            var compiledEntries = BuildEntries(entries, expectedIds, errors);
            foreach (var expectedId in expectedIds)
            {
                if (!compiledEntries.Any(entry => string.Equals(
                    entry.definitionId,
                    expectedId,
                    StringComparison.Ordinal)))
                {
                    errors.Add("Missing presentation entry for definition '" + expectedId + "'.");
                }
            }

            if (errors.Count > 0)
            {
                throw new ItemPresentationCatalogValidationException(errors);
            }

            var catalog = new ItemPresentationCatalogDocument
            {
                formatVersion = ItemPresentationCatalogFormat.Version,
                sourceCatalogRevision = sourceCatalogRevision,
                presentationRevision = string.Empty,
                entries = compiledEntries
                    .OrderBy(entry => entry.definitionId, StringComparer.Ordinal)
                    .ToArray()
            };
            catalog.presentationRevision = ComputeRevision(catalog);
            return catalog;
        }

        private static HashSet<string> BuildExpectedIds(
            IEnumerable<string> expectedDefinitionIds,
            ICollection<string> errors)
        {
            var expectedIds = new HashSet<string>(StringComparer.Ordinal);
            if (expectedDefinitionIds == null)
            {
                errors.Add("Expected definition ids are required.");
                return expectedIds;
            }

            foreach (var definitionId in expectedDefinitionIds)
            {
                if (string.IsNullOrWhiteSpace(definitionId))
                {
                    errors.Add("Expected definition ids must not contain empty values.");
                    continue;
                }

                if (!expectedIds.Add(definitionId))
                {
                    errors.Add("Expected definition ids contain duplicate id '" + definitionId + "'.");
                }
            }

            if (expectedIds.Count == 0)
            {
                errors.Add("At least one expected definition id is required.");
            }

            return expectedIds;
        }

        private static ItemPresentationEntry[] BuildEntries(
            IEnumerable<ItemPresentationEntry> entries,
            ISet<string> expectedIds,
            ICollection<string> errors)
        {
            if (entries == null)
            {
                errors.Add("Presentation entries are required.");
                return Array.Empty<ItemPresentationEntry>();
            }

            var result = new List<ItemPresentationEntry>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var entry in entries)
            {
                var path = "entries[" + index + "]";
                index++;
                if (entry == null)
                {
                    errors.Add(path + " must not be null.");
                    continue;
                }

                var definitionId = entry.definitionId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(definitionId))
                {
                    errors.Add(path + ".definitionId is required.");
                }
                else
                {
                    if (!seenIds.Add(definitionId))
                    {
                        errors.Add("Presentation entries contain duplicate definition id '"
                            + definitionId + "'.");
                    }

                    if (!expectedIds.Contains(definitionId))
                    {
                        errors.Add("Presentation entry references unknown definition '"
                            + definitionId + "'.");
                    }
                }

                var iconResourcePath = NormalizeOptional(entry.iconResourcePath);
                ValidateResourcePath(iconResourcePath, path + ".iconResourcePath", errors);
                var localizationKey = NormalizeRequired(
                    entry.localizationKey,
                    path + ".localizationKey",
                    errors);
                var fallbackDisplayName = NormalizeRequired(
                    entry.fallbackDisplayName,
                    path + ".fallbackDisplayName",
                    errors);

                result.Add(new ItemPresentationEntry
                {
                    definitionId = definitionId,
                    iconResourcePath = iconResourcePath,
                    localizationKey = localizationKey,
                    fallbackDisplayName = fallbackDisplayName,
                    prefabPresentationKey = NormalizeOptional(entry.prefabPresentationKey)
                });
            }

            return result.ToArray();
        }

        private static void ValidateResourcePath(
            string resourcePath,
            string path,
            ICollection<string> errors)
        {
            if (resourcePath.Length == 0)
            {
                return;
            }

            if (resourcePath.StartsWith("/", StringComparison.Ordinal)
                || resourcePath.EndsWith("/", StringComparison.Ordinal)
                || resourcePath.Contains("\\")
                || resourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || resourcePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
                || resourcePath.Split('/').Any(segment => segment == ".." || segment.Length == 0))
            {
                errors.Add(path + " must be a relative Unity Resources path.");
                return;
            }

            if (UnsupportedResourceExtensions.Any(extension => resourcePath.EndsWith(
                extension,
                StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(path + " must not include an asset file extension.");
            }
        }

        private static string NormalizeRequired(
            string value,
            string path,
            ICollection<string> errors)
        {
            var normalized = NormalizeOptional(value);
            if (normalized.Length == 0)
            {
                errors.Add(path + " is required.");
            }

            return normalized;
        }

        private static string NormalizeOptional(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string ComputeRevision(ItemPresentationCatalogDocument catalog)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(ItemPresentationCatalogFormat.Version);
                writer.Write(catalog.sourceCatalogRevision);
                writer.Write(catalog.entries.Length);
                foreach (var entry in catalog.entries)
                {
                    writer.Write(entry.definitionId);
                    writer.Write(entry.iconResourcePath);
                    writer.Write(entry.localizationKey);
                    writer.Write(entry.fallbackDisplayName);
                    writer.Write(entry.prefabPresentationKey);
                }

                writer.Flush();
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(stream.ToArray());
                    var builder = new StringBuilder(hash.Length * 2);
                    foreach (var value in hash)
                    {
                        builder.Append(value.ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class ItemPresentationCatalogIndex
    {
        private readonly Dictionary<string, ItemPresentationEntry> entries;
        private readonly Dictionary<string, Sprite> loadedIcons =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);

        private ItemPresentationCatalogIndex(ItemPresentationCatalogDocument catalog)
        {
            Catalog = catalog;
            entries = catalog.entries.ToDictionary(
                entry => entry.definitionId,
                entry => entry,
                StringComparer.Ordinal);
        }

        public ItemPresentationCatalogDocument Catalog { get; }

        public static bool TryCreate(
            ItemPresentationCatalogDocument catalog,
            string expectedSourceCatalogRevision,
            IEnumerable<string> expectedDefinitionIds,
            out ItemPresentationCatalogIndex index,
            out string error)
        {
            index = null;
            error = string.Empty;
            if (catalog == null)
            {
                error = "Item presentation catalog is missing.";
                return false;
            }

            if (catalog.formatVersion != ItemPresentationCatalogFormat.Version)
            {
                error = "Item presentation catalog format version is unsupported.";
                return false;
            }

            if (!string.Equals(
                catalog.sourceCatalogRevision,
                expectedSourceCatalogRevision,
                StringComparison.Ordinal))
            {
                error = "Item presentation catalog source revision does not match gameplay catalog.";
                return false;
            }

            try
            {
                var compiled = ItemPresentationCatalogCompiler.Compile(
                    catalog.sourceCatalogRevision,
                    expectedDefinitionIds,
                    catalog.entries);
                if (!string.Equals(
                    compiled.presentationRevision,
                    catalog.presentationRevision,
                    StringComparison.Ordinal))
                {
                    error = "Item presentation catalog revision is stale.";
                    return false;
                }

                index = new ItemPresentationCatalogIndex(compiled);
                return true;
            }
            catch (ItemPresentationCatalogValidationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public bool TryGet(string definitionId, out ItemPresentationEntry entry)
        {
            return entries.TryGetValue(definitionId, out entry);
        }

        public Sprite LoadIcon(string definitionId)
        {
            ItemPresentationEntry entry;
            if (!entries.TryGetValue(definitionId, out entry)
                || string.IsNullOrWhiteSpace(entry.iconResourcePath))
            {
                return null;
            }

            Sprite cached;
            if (loadedIcons.TryGetValue(entry.iconResourcePath, out cached))
            {
                return cached;
            }

            var icon = Resources.Load<Sprite>(entry.iconResourcePath);
            loadedIcons[entry.iconResourcePath] = icon;
            return icon;
        }
    }

    public static class ItemPresentationCatalogJson
    {
        public static ItemPresentationCatalogDocument Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Item presentation catalog JSON is empty.");
            }

            try
            {
                return JsonUtility.FromJson<ItemPresentationCatalogDocument>(json)
                    ?? throw new InvalidDataException(
                        "Item presentation catalog JSON has no root object.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Item presentation catalog JSON is malformed: " + exception.Message,
                    exception);
            }
        }

        public static string Serialize(ItemPresentationCatalogDocument catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            return NormalizeLineEndings(JsonUtility.ToJson(catalog, true)) + "\n";
        }

        private static string NormalizeLineEndings(string value)
        {
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }

    public static class ItemPresentationCatalogLoader
    {
        public const string ResourcePath = "Items/Presentation/item-presentation-catalog";

        private static string cachedKey;
        private static ItemPresentationCatalogIndex cachedIndex;

        public static bool TryLoadOnce(
            string expectedSourceCatalogRevision,
            IEnumerable<string> expectedDefinitionIds,
            out ItemPresentationCatalogIndex index,
            out string error)
        {
            var expectedIds = expectedDefinitionIds == null
                ? Array.Empty<string>()
                : expectedDefinitionIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var cacheKey = expectedSourceCatalogRevision + "|" + string.Join("|", expectedIds);
            if (cachedIndex != null && string.Equals(cachedKey, cacheKey, StringComparison.Ordinal))
            {
                index = cachedIndex;
                error = string.Empty;
                return true;
            }

            var asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset == null)
            {
                index = null;
                error = "Item presentation catalog resource is missing.";
                return false;
            }

            ItemPresentationCatalogDocument document;
            try
            {
                document = ItemPresentationCatalogJson.Deserialize(asset.text);
            }
            catch (Exception exception)
            {
                index = null;
                error = exception.Message;
                return false;
            }

            if (!ItemPresentationCatalogIndex.TryCreate(
                document,
                expectedSourceCatalogRevision,
                expectedIds,
                out index,
                out error))
            {
                return false;
            }

            cachedKey = cacheKey;
            cachedIndex = index;
            return true;
        }

        public static void ResetCache()
        {
            cachedKey = null;
            cachedIndex = null;
        }
    }
}
