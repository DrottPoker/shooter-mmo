using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Text
{
    // Text that no content owns, such as the messages for the codes that the servers send, is
    // written by hand in the source locale, one file per table below "content/text". The text
    // table compiler turns each file into the text table of the same id.
    public static class TextSourceFormat
    {
        public const int Version = 1;
        public const string DirectoryName = "text";
        public const string FileSuffix = ".json";
    }

    [Serializable]
    public sealed class TextSourceDocument
    {
        public int FormatVersion;
        public string TableId;
        public TextSourceEntry[] Entries;
    }

    [Serializable]
    public sealed class TextSourceEntry
    {
        public string Key;
        public string Text;
    }

    public static class TextSource
    {
        // The tables that are written by hand. Every other table is extracted from content.
        public static readonly IReadOnlyList<string> TableIds = Array.AsReadOnly(
            new[] { TextTableIds.Messages, TextTableIds.Ui });

        public static TextTableDocument Compile(TextSourceDocument source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var errors = new List<string>();
            if (source.FormatVersion != TextSourceFormat.Version)
            {
                errors.Add("formatVersion must be " + TextSourceFormat.Version + ".");
            }

            var known = false;
            foreach (var tableId in TableIds)
            {
                known |= string.Equals(tableId, source.TableId, StringComparison.Ordinal);
            }

            if (!known)
            {
                errors.Add("tableId '" + source.TableId + "' is not a table that is written by hand.");
            }

            var entries = new List<TextTableEntry>();
            var sourceEntries = source.Entries ?? Array.Empty<TextSourceEntry>();
            for (var index = 0; index < sourceEntries.Length; index++)
            {
                var entry = sourceEntries[index];
                var path = "entries[" + index + "]";
                if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
                {
                    errors.Add(path + " needs a key and a text.");
                    continue;
                }

                if (!TextTemplate.TryReadPlaceholders(entry.Text, out _))
                {
                    errors.Add(
                        path + " '" + entry.Key + "' uses braces for something else than a "
                        + "placeholder such as {name}.");
                }

                entries.Add(new TextTableEntry { Key = entry.Key, Text = entry.Text });
            }

            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }

            var table = TextTable.Create(source.TableId, TextTableFormat.SourceLocale, entries);
            RequireMessageKeys(table);
            RequireUiKeys(table);
            return table;
        }

        // The ui table holds the client's own texts, each below "ui/".
        private static void RequireUiKeys(TextTableDocument table)
        {
            if (!string.Equals(table.TableId, TextTableIds.Ui, StringComparison.Ordinal))
            {
                return;
            }

            var errors = new List<string>();
            foreach (var entry in table.Entries)
            {
                if (!TextKeys.IsUi(entry.Key))
                {
                    errors.Add("'" + entry.Key + "' is not a ui key such as 'ui/inventory/title'.");
                }
            }

            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }
        }

        // The messages table holds one text per code and the text for a code without one.
        private static void RequireMessageKeys(TextTableDocument table)
        {
            if (!string.Equals(table.TableId, TextTableIds.Messages, StringComparison.Ordinal))
            {
                return;
            }

            var errors = new List<string>();
            var unlisted = TextKeys.UnlistedMessage();
            var hasUnlisted = false;
            foreach (var entry in table.Entries)
            {
                if (string.Equals(entry.Key, unlisted, StringComparison.Ordinal))
                {
                    hasUnlisted = true;
                    continue;
                }

                if (!TextKeys.IsMessage(entry.Key))
                {
                    errors.Add(
                        "'" + entry.Key + "' is not a message key such as '"
                        + TextKeys.Message("code") + "'.");
                }
            }

            if (!hasUnlisted)
            {
                errors.Add("The text '" + unlisted + "' for a code without a text is missing.");
            }

            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }
        }
    }
}
