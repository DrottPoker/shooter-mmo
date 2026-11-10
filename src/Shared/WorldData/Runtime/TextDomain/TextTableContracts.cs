using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Text
{
    // Player-facing text lives in text tables, one per content domain and locale. The gameplay
    // catalogs and the realtime protocol carry ids, and a client looks the text up by key.
    public static class TextTableFormat
    {
        public const int Version = 1;

        // The locale that the content is authored in. Its tables are compiled from the content.
        public const string SourceLocale = "en";

        public const string DirectoryName = "Text";
        public const string FileSuffix = ".text-table.json";
        public const int MaximumKeyLength = 512;
        public const int MaximumTextLength = 2000;
    }

    // One table per content domain, so a change to one domain rewrites only its own table.
    public static class TextTableIds
    {
        public const string Items = "items";
        public const string Actors = "actors";
        public const string Quests = "quests";

        public static readonly IReadOnlyList<string> All = Array.AsReadOnly(
            new[] { Actors, Items, Quests });
    }

    [Serializable]
    public sealed class TextTableDocument
    {
        public int FormatVersion;
        public string TableId;
        public string Locale;
        public string Revision;
        public TextTableEntry[] Entries;
    }

    [Serializable]
    public sealed class TextTableEntry
    {
        public string Key;
        public string Text;
    }

    public sealed class TextTableValidationException : Exception
    {
        public TextTableValidationException(IReadOnlyList<string> errors)
            : base("Text table is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
