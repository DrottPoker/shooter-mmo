using System;
using System.Linq;

namespace Mmorpg.WorldData.Text
{
    // The camelCase field shape of a text table for Unity's JsonUtility, which reads fields by
    // their exact names. The Unity client reads the tables with it and checks each with
    // TextTable.Validate, so a new table field is added here too.
    [Serializable]
    public sealed class TextTableJsonDocument
    {
        public int formatVersion;
        public string tableId;
        public string locale;
        public string revision;
        public TextTableJsonEntry[] entries;

        public TextTableDocument ToRuntime()
        {
            return new TextTableDocument
            {
                FormatVersion = formatVersion,
                TableId = tableId,
                Locale = locale,
                Revision = revision,
                Entries = (entries ?? Array.Empty<TextTableJsonEntry>())
                    .Select(entry => entry == null
                        ? null
                        : new TextTableEntry { Key = entry.key, Text = entry.text })
                    .ToArray()
            };
        }
    }

    [Serializable]
    public sealed class TextTableJsonEntry
    {
        public string key;
        public string text;
    }
}
