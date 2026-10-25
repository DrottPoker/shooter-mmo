using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Mmorpg.WorldData.Items.Presentation
{
    public static class ItemPresentationCatalogFormat
    {
        // Version 2 replaced the Resources icon path with an Addressables icon address, version 3
        // added the garments an item shows and its dye colors, and version 4 the attachment it
        // holds on a socket.
        public const int Version = 4;

        // The dye channels a garment texture may have, and so the most dye colors of an item.
        public const int MaximumDyes = 3;

        // The most garments an item shows in one form.
        public const int MaximumGarments = 4;

        // Every icon is an Addressables Sprite in the client's item icon folder entry.
        public const string IconAddressPrefix = "Items/Icons/";
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
        public string iconAddress;
        public string localizationKey;
        public string fallbackDisplayName;
        public string prefabPresentationKey;

        // The garments the item shows on a body when it is worn, by the name of their garment
        // texture, such as WoolCloak; none for an item that shows no garment.
        public string[] garments = Array.Empty<string>();

        // The garments it shows instead while its wearer has lowered it, such as a hood pushed
        // back; none for an item that cannot be lowered.
        public string[] loweredGarments = Array.Empty<string>();

        // The colors of its garments' dye channels, "#RRGGBB" in channel order; none for their
        // natural colors.
        public string[] dyes = Array.Empty<string>();

        // The attachment it shows held on the socket of its equipment slot, by the name of its art
        // asset, such as IronSword; empty for an item that shows none.
        public string attachment = string.Empty;
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

                var iconAddress = NormalizeOptional(entry.iconAddress);
                ValidateIconAddress(iconAddress, path + ".iconAddress", errors);
                var localizationKey = NormalizeRequired(
                    entry.localizationKey,
                    path + ".localizationKey",
                    errors);
                var fallbackDisplayName = NormalizeRequired(
                    entry.fallbackDisplayName,
                    path + ".fallbackDisplayName",
                    errors);

                var garments = NormalizeNames(entry.garments, path + ".garments", errors);
                var loweredGarments = NormalizeNames(entry.loweredGarments, path + ".loweredGarments", errors);
                var dyes = NormalizeDyes(entry.dyes, path + ".dyes", errors);
                if (garments.Length == 0 && (loweredGarments.Length > 0 || dyes.Length > 0))
                {
                    errors.Add(path + " has lowered garments or dyes but shows no garments.");
                }

                var attachment = NormalizeOptional(entry.attachment);
                if (attachment.Any(character => character > 127 || !char.IsLetterOrDigit(character)))
                {
                    errors.Add(path + ".attachment must name an attachment with letters and digits.");
                }

                result.Add(new ItemPresentationEntry
                {
                    definitionId = definitionId,
                    iconAddress = iconAddress,
                    localizationKey = localizationKey,
                    fallbackDisplayName = fallbackDisplayName,
                    prefabPresentationKey = NormalizeOptional(entry.prefabPresentationKey),
                    garments = garments,
                    loweredGarments = loweredGarments,
                    dyes = dyes,
                    attachment = attachment
                });
            }

            return result.ToArray();
        }

        private static void ValidateIconAddress(
            string iconAddress,
            string path,
            ICollection<string> errors)
        {
            if (iconAddress.Length == 0)
            {
                return;
            }

            var relative = iconAddress.StartsWith(
                ItemPresentationCatalogFormat.IconAddressPrefix,
                StringComparison.Ordinal)
                ? iconAddress.Substring(ItemPresentationCatalogFormat.IconAddressPrefix.Length)
                : null;
            if (relative == null
                || relative.Length == 0
                || relative.EndsWith("/", StringComparison.Ordinal)
                || relative.Contains("\\")
                || relative.Split('/').Any(segment => segment == ".." || segment.Length == 0))
            {
                errors.Add(path + " must be an Addressables address below '"
                    + ItemPresentationCatalogFormat.IconAddressPrefix + "'.");
            }
        }

        // Garment names are letters and digits, each once, as the appearance catalogs name them.
        private static string[] NormalizeNames(
            string[] values,
            string path,
            ICollection<string> errors)
        {
            var names = (values ?? Array.Empty<string>()).Select(NormalizeOptional).ToArray();
            if (names.Length > ItemPresentationCatalogFormat.MaximumGarments)
            {
                errors.Add(path + " lists more than " + ItemPresentationCatalogFormat.MaximumGarments + " garments.");
            }

            if (names.Any(name => name.Length == 0 || name.Any(character => character > 127 || !char.IsLetterOrDigit(character))))
            {
                errors.Add(path + " must name garments with letters and digits.");
            }

            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            {
                errors.Add(path + " names a garment twice.");
            }

            return names;
        }

        // Dye colors are "#RRGGBB" with upper-case hexadecimal digits, at most one per dye channel.
        private static string[] NormalizeDyes(
            string[] values,
            string path,
            ICollection<string> errors)
        {
            var dyes = (values ?? Array.Empty<string>()).Select(NormalizeOptional).ToArray();
            if (dyes.Length > ItemPresentationCatalogFormat.MaximumDyes)
            {
                errors.Add(path + " lists more than " + ItemPresentationCatalogFormat.MaximumDyes + " dye colors.");
            }

            if (dyes.Any(dye => dye.Length != 7 || dye[0] != '#' || dye.Skip(1).Any(character =>
                    !((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F')))))
            {
                errors.Add(path + " must hold colors as #RRGGBB with upper-case hexadecimal digits.");
            }

            return dyes;
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
                    writer.Write(entry.iconAddress);
                    writer.Write(entry.localizationKey);
                    writer.Write(entry.fallbackDisplayName);
                    writer.Write(entry.prefabPresentationKey);
                    WriteList(writer, entry.garments);
                    WriteList(writer, entry.loweredGarments);
                    WriteList(writer, entry.dyes);
                    writer.Write(entry.attachment);
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

        private static void WriteList(BinaryWriter writer, string[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value);
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
}
