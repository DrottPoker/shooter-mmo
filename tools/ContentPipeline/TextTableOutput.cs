using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Tools.ContentPipeline;

// Every content compiler writes the text table of its domain next to its compiled data, so
// one run keeps both current and --verify covers both.
public static class TextTableOutput
{
    public static string Serialize(TextTableDocument table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return StrictContentJson.Serialize(table);
    }

    public static TextTableDocument Deserialize(string json)
    {
        var table = StrictContentJson.Deserialize<TextTableDocument>(json, "Text table");
        TextTable.Validate(table);
        return table;
    }

    public static void WriteOrVerify(string generatedRoot, TextTableDocument table, bool verifyOnly)
    {
        ArgumentNullException.ThrowIfNull(table);
        GeneratedOutput.WriteOrVerifyText(
            ContentLayout.GeneratedTextTablePath(generatedRoot, table.Locale, table.TableId),
            Serialize(table),
            verifyOnly,
            $"Text table '{table.TableId}' for locale '{table.Locale}'");
        Console.WriteLine(
            "Text table {0} {1} for locale {2} at revision {3} with {4} texts.",
            table.TableId,
            verifyOnly ? "verified" : "compiled",
            table.Locale,
            table.Revision,
            table.Entries.Length);
    }
}
