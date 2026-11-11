using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // The kinds of content file that GameService serves by revision, as its manifest names them.
    public static class ClientContentKinds
    {
        public const string ItemCatalog = "item-catalog";
        public const string QuestCatalog = "quest-catalog";
        public const string TextTable = "text-table";

        // The id of the one item catalog and the one quest catalog.
        public const string CoreCatalogId = "core";
    }

    // Where a resolved content file came from.
    public enum ClientContentSource
    {
        Bundled,
        Cache,
        Downloaded
    }

    // Checks a cached or downloaded file as its loader does, which computes its revision from its
    // content again, and returns the header that the checked content states.
    public delegate bool ClientContentValidator(string text, out ClientContentHeader header, out string error);

    // The identity that a content file states about itself. Locale is empty for a catalog.
    public sealed class ClientContentHeader
    {
        public ClientContentHeader(string id, string locale, int formatVersion, string revision)
        {
            Id = id ?? string.Empty;
            Locale = locale ?? string.Empty;
            FormatVersion = formatVersion;
            Revision = revision ?? string.Empty;
        }

        public string Id { get; }

        public string Locale { get; }

        public int FormatVersion { get; }

        public string Revision { get; }

        // Reads only the header fields of a catalog or text table, without checking the content,
        // such as for a bundled file whose loader checks it later.
        public static bool TryRead(string json, out ClientContentHeader header)
        {
            header = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var document = JsonUtility.FromJson<ClientContentHeaderJson>(json);
                if (document == null)
                {
                    return false;
                }

                header = new ClientContentHeader(
                    string.IsNullOrEmpty(document.catalogId) ? document.tableId : document.catalogId,
                    document.locale,
                    document.formatVersion,
                    document.revision);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    [Serializable]
    internal sealed class ClientContentHeaderJson
    {
        public int formatVersion;
        public string catalogId;
        public string tableId;
        public string locale;
        public string revision;
    }

    // One file of the content set: what the client expects of it, its bundled copy, and how a
    // cached or downloaded copy is checked. Locale is empty for a catalog.
    public sealed class ClientContentRequest
    {
        public ClientContentRequest(
            string kind,
            string id,
            string locale,
            int formatVersion,
            string bundledText,
            ClientContentValidator validator)
        {
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Locale = locale ?? string.Empty;
            FormatVersion = formatVersion;
            BundledText = bundledText ?? string.Empty;
            Validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        public string Kind { get; }

        public string Id { get; }

        public string Locale { get; }

        // The format version this client build reads.
        public int FormatVersion { get; }

        public string BundledText { get; }

        public ClientContentValidator Validator { get; }

        // Such as "text-table en/items" or "item-catalog core".
        public string Name
        {
            get { return Kind + " " + (Locale.Length > 0 ? Locale + "/" + Id : Id); }
        }
    }

    // One resolved content file. Revision is empty for a bundled file whose header is unreadable.
    public sealed class ClientContentFile
    {
        public ClientContentFile(
            string kind,
            string id,
            string locale,
            string revision,
            ClientContentSource source,
            string text)
        {
            Kind = kind;
            Id = id;
            Locale = locale ?? string.Empty;
            Revision = revision ?? string.Empty;
            Source = source;
            Text = text ?? string.Empty;
        }

        public string Kind { get; }

        public string Id { get; }

        public string Locale { get; }

        public string Revision { get; }

        public ClientContentSource Source { get; }

        public string Text { get; }
    }

    // The content the client runs with until it exits: every file of the set, and why the
    // client fell back to the bundled set, if it did.
    public sealed class ClientContentReport
    {
        // The served content has a format this client build cannot read.
        public const string UpdateRequiredCode = "client_update_required";

        // The served content could not be fetched or checked, so the bundled set is used.
        public const string UnavailableCode = "content_unavailable";

        public ClientContentReport(
            IReadOnlyList<ClientContentFile> files,
            string fallbackCode,
            string fallbackDiagnostic,
            IReadOnlyList<string> warnings)
        {
            Files = files ?? Array.Empty<ClientContentFile>();
            FallbackCode = fallbackCode ?? string.Empty;
            FallbackDiagnostic = fallbackDiagnostic ?? string.Empty;
            Warnings = warnings ?? Array.Empty<string>();
        }

        public IReadOnlyList<ClientContentFile> Files { get; }

        // Empty when the served content was resolved.
        public string FallbackCode { get; }

        public string FallbackDiagnostic { get; }

        // Problems that did not change the result, such as a cache that could not be written.
        public IReadOnlyList<string> Warnings { get; }

        public bool FellBackToBundled
        {
            get { return FallbackCode.Length > 0; }
        }

        public bool UpdateRequired
        {
            get { return string.Equals(FallbackCode, UpdateRequiredCode, StringComparison.Ordinal); }
        }

        // The bundled set as it is, for a client that has not checked its content.
        public static ClientContentReport Bundled(
            IReadOnlyList<ClientContentRequest> requests,
            string fallbackCode,
            string fallbackDiagnostic,
            IReadOnlyList<string> warnings)
        {
            var files = (requests ?? Array.Empty<ClientContentRequest>())
                .Select(request => new ClientContentFile(
                    request.Kind,
                    request.Id,
                    request.Locale,
                    ClientContentHeader.TryRead(request.BundledText, out var header) ? header.Revision : string.Empty,
                    ClientContentSource.Bundled,
                    request.BundledText))
                .ToArray();
            return new ClientContentReport(files, fallbackCode, fallbackDiagnostic, warnings);
        }

        // The text of a catalog, or empty when the set holds none.
        public string TextOf(string kind, string id)
        {
            var file = Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Kind, kind, StringComparison.Ordinal)
                && string.Equals(candidate.Id, id, StringComparison.Ordinal));
            return file != null ? file.Text : string.Empty;
        }

        // The texts of every file of a kind, such as all text tables, in the order of the set.
        public IReadOnlyList<string> TextsOf(string kind)
        {
            return Files
                .Where(file => string.Equals(file.Kind, kind, StringComparison.Ordinal))
                .Select(file => file.Text)
                .ToArray();
        }
    }
}
