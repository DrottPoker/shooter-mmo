using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // The list of content files that GameService serves, as GET /api/content returns it.
    public sealed class ClientContentManifest
    {
        // The manifest format this client build reads.
        public const int SupportedFormatVersion = 1;

        private const int RevisionLength = 64;

        private readonly ClientContentManifestEntry[] files;

        private ClientContentManifest(int formatVersion, ClientContentManifestEntry[] files)
        {
            FormatVersion = formatVersion;
            this.files = files;
        }

        public int FormatVersion { get; }

        public IReadOnlyList<ClientContentManifestEntry> Files
        {
            get { return files; }
        }

        // Accepts a manifest whose entries each name a kind, an id, a SHA-256 revision, a size,
        // and a URL, and list no file twice. Kinds this client does not know are kept, unused.
        public static bool TryParse(string json, out ClientContentManifest manifest, out string error)
        {
            manifest = null;
            error = string.Empty;
            ClientContentManifestJson document;
            try
            {
                document = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<ClientContentManifestJson>(json);
            }
            catch (ArgumentException exception)
            {
                error = "The content manifest is not valid JSON: " + exception.Message;
                return false;
            }

            if (document == null)
            {
                error = "The content manifest is empty.";
                return false;
            }

            var entries = document.files ?? Array.Empty<ClientContentManifestFileJson>();
            var parsed = new ClientContentManifestEntry[entries.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry == null
                    || string.IsNullOrEmpty(entry.kind)
                    || string.IsNullOrEmpty(entry.id)
                    || !IsRevision(entry.revision)
                    || entry.sizeBytes < 0
                    || string.IsNullOrEmpty(entry.url))
                {
                    error = "Content manifest file " + index + " lacks a kind, an id, a revision, a size, or a URL.";
                    return false;
                }

                parsed[index] = new ClientContentManifestEntry(
                    entry.kind,
                    entry.id,
                    entry.locale,
                    entry.formatVersion,
                    entry.revision,
                    entry.sizeBytes,
                    entry.url);
                if (!names.Add(parsed[index].Name))
                {
                    error = "The content manifest lists " + parsed[index].Name + " twice.";
                    return false;
                }
            }

            manifest = new ClientContentManifest(document.formatVersion, parsed);
            return true;
        }

        public ClientContentManifestEntry Find(string kind, string id, string locale)
        {
            foreach (var file in files)
            {
                if (string.Equals(file.Kind, kind, StringComparison.Ordinal)
                    && string.Equals(file.Id, id, StringComparison.Ordinal)
                    && string.Equals(file.Locale, locale ?? string.Empty, StringComparison.Ordinal))
                {
                    return file;
                }
            }

            return null;
        }

        // A SHA-256 revision in lowercase hex, which is also safe as a cache file name.
        public static bool IsRevision(string revision)
        {
            if (revision == null || revision.Length != RevisionLength)
            {
                return false;
            }

            foreach (var character in revision)
            {
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class ClientContentManifestEntry
    {
        public ClientContentManifestEntry(
            string kind,
            string id,
            string locale,
            int formatVersion,
            string revision,
            long sizeBytes,
            string url)
        {
            Kind = kind;
            Id = id;
            Locale = locale ?? string.Empty;
            FormatVersion = formatVersion;
            Revision = revision;
            SizeBytes = sizeBytes;
            Url = url;
        }

        public string Kind { get; }

        public string Id { get; }

        public string Locale { get; }

        public int FormatVersion { get; }

        public string Revision { get; }

        public long SizeBytes { get; }

        // Relative to the GameService base URL, or absolute.
        public string Url { get; }

        public string Name
        {
            get { return Kind + " " + (Locale.Length > 0 ? Locale + "/" + Id : Id); }
        }
    }

    [Serializable]
    internal sealed class ClientContentManifestJson
    {
        public int formatVersion;
        public ClientContentManifestFileJson[] files;
    }

    [Serializable]
    internal sealed class ClientContentManifestFileJson
    {
        public string kind;
        public string id;
        public string locale;
        public int formatVersion;
        public string revision;
        public long sizeBytes;
        public string url;
    }
}
