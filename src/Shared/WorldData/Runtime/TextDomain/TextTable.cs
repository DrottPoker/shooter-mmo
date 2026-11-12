using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.WorldData.Text
{
    // Builds and checks text tables. Equal entries build a byte-for-byte equal table: the
    // entries are sorted by key and the revision covers every key and text.
    public static class TextTable
    {
        private const int MaximumLocaleLength = 35;

        public static TextTableDocument Create(
            string tableId,
            string locale,
            IEnumerable<TextTableEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var document = new TextTableDocument
            {
                FormatVersion = TextTableFormat.Version,
                TableId = tableId,
                Locale = locale,
                Revision = string.Empty,
                Entries = entries
                    .Where(entry => entry != null)
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToArray()
            };
            var errors = new List<string>();
            ValidateStructure(document, errors);
            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }

            Stamp(document);
            return document;
        }

        // Writes the revision from the content. The hash coverage tests require every serialized
        // field to change it.
        public static void Stamp(TextTableDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            document.Revision = ComputeRevision(document);
        }

        // A loaded table must be exactly what a compiler wrote: its structure is checked and
        // its revision is computed again and compared.
        public static void Validate(TextTableDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var errors = new List<string>();
            if (document.FormatVersion != TextTableFormat.Version)
            {
                errors.Add("text table formatVersion must be " + TextTableFormat.Version + ".");
            }

            ValidateStructure(document, errors);
            if (errors.Count == 0
                && !string.Equals(document.Revision, ComputeRevision(document), StringComparison.Ordinal))
            {
                errors.Add("text table revision does not match its content.");
            }

            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }
        }

        public static bool IsValidLocale(string locale)
        {
            if (string.IsNullOrEmpty(locale)
                || locale.Length > MaximumLocaleLength
                || !IsLowerAsciiLetter(locale[0])
                || locale[locale.Length - 1] == '-')
            {
                return false;
            }

            foreach (var character in locale)
            {
                if (!IsLowerAsciiLetter(character)
                    && (character < '0' || character > '9')
                    && character != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateStructure(TextTableDocument document, List<string> errors)
        {
            if (!TextTableIds.All.Contains(document.TableId, StringComparer.Ordinal))
            {
                errors.Add("text table tableId '" + document.TableId + "' is not a known table.");
            }

            if (!IsValidLocale(document.Locale))
            {
                errors.Add("text table locale '" + document.Locale + "' must be a lowercase locale such as 'en' or 'pt-br'.");
            }

            if (document.Entries == null)
            {
                errors.Add("text table must list its entries.");
                return;
            }

            string previousKey = null;
            for (var index = 0; index < document.Entries.Length; index++)
            {
                var entry = document.Entries[index];
                var path = "entries[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                if (entry == null)
                {
                    errors.Add(path + " is required.");
                    continue;
                }

                if (!TextKeys.IsValid(entry.Key))
                {
                    errors.Add(path + ".key '" + entry.Key + "' is not a valid text key.");
                }
                else if (previousKey != null)
                {
                    var order = string.CompareOrdinal(previousKey, entry.Key);
                    if (order == 0)
                    {
                        errors.Add(path + ".key '" + entry.Key + "' is listed twice.");
                    }
                    else if (order > 0)
                    {
                        errors.Add(path + ".key '" + entry.Key + "' is not in key order.");
                    }
                }

                previousKey = TextKeys.IsValid(entry.Key) ? entry.Key : previousKey;
                ValidateText(entry.Text, path + ".text", errors);
            }
        }

        // A text is never empty, has no whitespace around it, and holds no control character
        // but the line feed, so every client draws it the same way.
        private static void ValidateText(string text, string path, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                errors.Add(path + " is required.");
                return;
            }

            if (text.Length > TextTableFormat.MaximumTextLength)
            {
                errors.Add(path + " must be at most " + TextTableFormat.MaximumTextLength + " characters.");
            }

            if (!string.Equals(text, text.Trim(), StringComparison.Ordinal))
            {
                errors.Add(path + " must not start or end with whitespace.");
            }

            if (text.Any(character => char.IsControl(character) && character != '\n'))
            {
                errors.Add(path + " must not contain control characters other than a line feed.");
            }
        }

        private static string ComputeRevision(TextTableDocument document)
        {
            var values = new List<string>
            {
                "text-table",
                document.FormatVersion.ToString(CultureInfo.InvariantCulture),
                document.TableId,
                document.Locale
            };
            foreach (var entry in document.Entries)
            {
                values.Add(entry.Key);

                // A text may hold line feeds, so its length keeps two entries from reading as one.
                values.Add(entry.Text.Length.ToString(CultureInfo.InvariantCulture));
                values.Add(entry.Text);
            }

            return WorldActorCompiler.ComputeSha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", values)));
        }

        private static bool IsLowerAsciiLetter(char value)
        {
            return value >= 'a' && value <= 'z';
        }
    }
}
