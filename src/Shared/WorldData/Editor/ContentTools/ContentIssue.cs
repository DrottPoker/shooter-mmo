using System;
using System.Text.RegularExpressions;

namespace Mmorpg.WorldData.Editor.ContentTools
{
    public enum ContentIssueSeverity
    {
        Error,
        Warning
    }

    // A validation finding. EntityKey names the edited entry, such as an item definition id,
    // or is empty for a finding about the whole file. Field is the path inside the entry, such
    // as "unitWeight" or "bag.slots[0].kind", or empty when the finding is about the entry.
    public sealed class ContentIssue
    {
        public ContentIssue(
            string entityKey,
            string field,
            string message,
            ContentIssueSeverity severity = ContentIssueSeverity.Error)
        {
            EntityKey = entityKey ?? string.Empty;
            Field = field ?? string.Empty;
            Message = message ?? string.Empty;
            Severity = severity;
        }

        public string EntityKey { get; }

        public string Field { get; }

        public string Message { get; }

        public ContentIssueSeverity Severity { get; }

        // True when the issue concerns the field or something inside it, so a finding about
        // bag.slots[0].kind also marks the bag field.
        public bool Concerns(string field)
        {
            if (string.IsNullOrEmpty(field) || Field.Length == 0)
            {
                return false;
            }

            return Field.StartsWith(field, StringComparison.Ordinal)
                && (Field.Length == field.Length || Field[field.Length] == '.' || Field[field.Length] == '[');
        }
    }

    // The shared content compilers start every error with the path of the value it concerns,
    // such as "definitions[3].unitWeight must not be negative.".
    public static class ContentIssuePath
    {
        private static readonly Regex DuplicatePattern = new Regex(
            @"^(?<collection>[A-Za-z][A-Za-z0-9]*) contains duplicate id '(?<id>[^']*)'",
            RegexOptions.CultureInvariant);

        private static readonly Regex Pattern = new Regex(
            @"^(?<collection>[A-Za-z][A-Za-z0-9]*)\[(?<index>[0-9]+)\](?:\.(?<field>[A-Za-z0-9_.\[\]]+))?(?=[\s.,:]|$)",
            RegexOptions.CultureInvariant);

        // A duplicate id is reported for the collection, such as
        // "definitions contains duplicate id 'sword'.", so it concerns every entry with that id.
        public static bool TryParseDuplicateId(string error, out string collection, out string id)
        {
            collection = string.Empty;
            id = string.Empty;
            var match = DuplicatePattern.Match(error ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            collection = match.Groups["collection"].Value;
            id = match.Groups["id"].Value;
            return true;
        }

        // Detail is the error after the entry path, such as "unitWeight must not be negative.".
        public static bool TryParse(
            string error,
            out string collection,
            out int index,
            out string field,
            out string detail)
        {
            collection = string.Empty;
            index = -1;
            field = string.Empty;
            detail = error ?? string.Empty;
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            var match = Pattern.Match(error);
            if (!match.Success || !int.TryParse(match.Groups["index"].Value, out index))
            {
                index = -1;
                return false;
            }

            collection = match.Groups["collection"].Value;
            field = match.Groups["field"].Value.TrimEnd('.');
            var entryPathLength = match.Groups["index"].Index + match.Groups["index"].Length + 1;
            detail = error.Substring(entryPathLength).TrimStart('.', ' ');
            return true;
        }

        // Turns a compiler error into a finding about the entry and field it names. KeyAt maps
        // the index in the collection to the entry key; an error that names no known entry
        // becomes a finding about the whole content.
        public static ContentIssue ToIssue(
            string error,
            string collection,
            Func<int, string> keyAt,
            string fieldPrefix = "")
        {
            if (TryParse(error, out var parsedCollection, out var index, out var field, out var detail)
                && string.Equals(parsedCollection, collection, StringComparison.Ordinal))
            {
                var key = index >= 0 ? keyAt(index) : null;
                if (!string.IsNullOrEmpty(key))
                {
                    return new ContentIssue(key, field.Length == 0 ? string.Empty : fieldPrefix + field, detail);
                }
            }

            if (TryParseDuplicateId(error, out var duplicateCollection, out var id)
                && string.Equals(duplicateCollection, collection, StringComparison.Ordinal)
                && id.Length > 0)
            {
                return new ContentIssue(id, "id", "id '" + id + "' is used by more than one entry.");
            }

            return new ContentIssue(string.Empty, string.Empty, error);
        }
    }
}
