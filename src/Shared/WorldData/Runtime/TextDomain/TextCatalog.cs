using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.WorldData.Text
{
    // Every text of one locale, from all of its tables, ready to look up by key. A client
    // builds one from the tables it loaded and shows nothing but what it finds here.
    public sealed class TextCatalog
    {
        private readonly Dictionary<string, string> texts;

        private TextCatalog(string locale, string revision, Dictionary<string, string> texts)
        {
            Locale = locale;
            Revision = revision;
            this.texts = texts;
        }

        public string Locale { get; }

        // Changes when any text of any table changes.
        public string Revision { get; }

        public int Count
        {
            get { return texts.Count; }
        }

        // The tables must be valid, of one locale, and of different table ids, and no key may
        // appear in two of them.
        public static TextCatalog Create(IEnumerable<TextTableDocument> tables)
        {
            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }

            var ordered = tables
                .Where(table => table != null)
                .OrderBy(table => table.TableId, StringComparer.Ordinal)
                .ToArray();
            var errors = new List<string>();
            if (ordered.Length == 0)
            {
                errors.Add("a text catalog needs at least one text table.");
                throw new TextTableValidationException(errors);
            }

            foreach (var table in ordered)
            {
                TextTable.Validate(table);
            }

            var locale = ordered[0].Locale;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var tableIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in ordered)
            {
                if (!string.Equals(table.Locale, locale, StringComparison.Ordinal))
                {
                    errors.Add("text table '" + table.TableId + "' has locale '" + table.Locale
                        + "', but the catalog has locale '" + locale + "'.");
                }

                if (!tableIds.Add(table.TableId))
                {
                    errors.Add("text table '" + table.TableId + "' is listed twice.");
                    continue;
                }

                foreach (var entry in table.Entries)
                {
                    if (texts.ContainsKey(entry.Key))
                    {
                        errors.Add("text key '" + entry.Key + "' is in more than one table.");
                        continue;
                    }

                    texts.Add(entry.Key, entry.Text);
                }
            }

            if (errors.Count > 0)
            {
                throw new TextTableValidationException(errors);
            }

            var revision = WorldActorCompiler.ComputeSha256Hex(Encoding.UTF8.GetBytes(string.Join(
                "\n",
                new[] { "text-catalog", locale }.Concat(
                    ordered.Select(table => table.TableId + "=" + table.Revision)))));
            return new TextCatalog(locale, revision, texts);
        }

        // A catalog without texts, for a client whose tables could not be read: every text
        // then shows as its key.
        public static TextCatalog Empty(string locale)
        {
            return new TextCatalog(
                locale,
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        public bool TryGet(string key, out string text)
        {
            if (key == null)
            {
                text = null;
                return false;
            }

            return texts.TryGetValue(key, out text);
        }

        // A missing text shows its key in brackets, so it is found on screen instead of
        // passing as an empty label.
        public string Get(string key)
        {
            return TryGet(key, out var text) ? text : Missing(key);
        }

        // For a text that content may leave out, such as the line of an objective.
        public string GetOrEmpty(string key)
        {
            return TryGet(key, out var text) ? text : string.Empty;
        }

        public static string Missing(string key)
        {
            return "[" + (key ?? string.Empty) + "]";
        }

        public override string ToString()
        {
            return "TextCatalog " + Locale + " " + Count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
