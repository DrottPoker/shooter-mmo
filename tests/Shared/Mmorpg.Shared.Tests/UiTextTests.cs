using System.Text.RegularExpressions;
using Mmorpg.Testing;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Shared.Tests;

// The client's own labels and sentences live in the ui text table, written by hand in
// content/text/ui.json, and the code names each by its key.
public sealed partial class UiTextTests
{
    private static readonly string ClientScripts =
        Path.Combine(RepositoryPaths.Root, "src", "Client", "Assets", "Scripts");

    [Fact]
    public void AUiSourceHoldsOnlyUiKeys()
    {
        var table = TextSource.Compile(Source(("ui/inventory/title", "Inventory")));
        Assert.Equal(TextTableIds.Ui, table.TableId);
        Assert.True(TextKeys.IsUi("ui/inventory/title"));
        Assert.False(TextKeys.IsUi("item/weapon.iron_sword/name"));
        Assert.False(TextKeys.IsUi("ui"));

        var errors = Assert.Throws<TextTableValidationException>(
            () => TextSource.Compile(Source(("message/item_not_found", "Gone.")))).Errors;
        Assert.Contains(errors, error => error.Contains("is not a ui key", StringComparison.Ordinal));
    }

    [Fact]
    public void ATextIsFormattedWithItsNamedValues()
    {
        var texts = TextCatalog.Create(
        [
            TextTable.Create(
                TextTableIds.Ui,
                TextTableFormat.SourceLocale,
                [
                    new TextTableEntry { Key = "ui/quests/reward-gold", Text = "{amount} {currency}" },
                    new TextTableEntry { Key = "ui/quests/back", Text = "Back" }
                ]),
            TextTable.Create(
                TextTableIds.Items,
                TextTableFormat.SourceLocale,
                [new TextTableEntry { Key = TextKeys.CurrencyName(), Text = "Gold" }])
        ]);

        Assert.Equal("Back", texts.Format("ui/quests/back"));
        Assert.Equal(
            "25 Gold",
            texts.Format(
                "ui/quests/reward-gold",
                MessageArgument.Text("amount", "25"),
                MessageArgument.TextKey("currency", TextKeys.CurrencyName())));
        Assert.Equal("[ui/quests/missing]", texts.Format("ui/quests/missing"));
    }

    // The pseudo-locale alters every text and leaves what code reads: a placeholder, a rich
    // text tag, and the keys.
    [Fact]
    public void ThePseudoLocaleAltersEveryTextAndKeepsPlaceholdersAndTags()
    {
        Assert.Equal("Réády tó túrn ín", PseudoText.Alter("Ready to turn in"));
        Assert.Equal("{quantity} x {item}", PseudoText.Alter("{quantity} x {item}"));
        Assert.Equal("<b>Séll</b> fór {amount}", PseudoText.Alter("<b>Sell</b> for {amount}"));
        Assert.Equal(string.Empty, PseudoText.Alter(null));

        var texts = TextCatalog.Create([CommittedUiTable()]);
        var pseudo = texts.ToPseudoLocale();

        Assert.Equal(PseudoText.Locale, pseudo.Locale);
        Assert.Equal(texts.Count, pseudo.Count);
        Assert.Equal("Báck", pseudo.Get("ui/quests/back"));
        Assert.Equal(
            "Ábándón Wolves? Íts prógréss ánd íts qúést ítéms áré lóst.",
            pseudo.Format("ui/quests/abandon-question", MessageArgument.Text("quest", "Wolves")));
        Assert.All(
            CommittedUiTable().Entries.Where(entry => entry.Text.IndexOfAny(['a', 'e', 'i', 'o', 'u']) >= 0
                && !entry.Text.StartsWith('{')),
            entry => Assert.NotEqual(entry.Text, pseudo.Get(entry.Key)));
    }

    // A key in the code without a text shows as "[key]" in the game, and a text without a key
    // in the code is dead. Both directions are checked against the client's sources.
    [Fact]
    public void TheClientUsesExactlyTheKeysOfTheUiTable()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(ClientScripts, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in UiKeyLiteral().Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        var table = CommittedUiTable().Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var withoutText = used.Where(key => !table.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var unused = table.Where(key => !used.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.True(withoutText.Length == 0, "content/text/ui.json has no text for: " + string.Join(", ", withoutText));
        Assert.True(unused.Length == 0, "No client code uses: " + string.Join(", ", unused));
    }

    [Fact]
    public void TheCommittedUiTableIsItsCompiledSource()
    {
        var source = StrictContentJson.DeserializeFile<TextSourceDocument>(
            ContentLayout.TextSourcePath(RepositoryPaths.Content, TextTableIds.Ui),
            "Text source");

        Assert.Equal(TextSource.Compile(source).Revision, CommittedUiTable().Revision);
    }

    private static TextTableDocument CommittedUiTable()
    {
        return TextTableOutput.Deserialize(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(RepositoryPaths.Generated, "en", TextTableIds.Ui)));
    }

    private static TextSourceDocument Source(params (string Key, string Text)[] entries)
    {
        return new TextSourceDocument
        {
            FormatVersion = TextSourceFormat.Version,
            TableId = TextTableIds.Ui,
            Entries = entries.Select(entry => new TextSourceEntry { Key = entry.Key, Text = entry.Text }).ToArray()
        };
    }

    [GeneratedRegex("\"(ui/[a-z0-9._/-]+)\"")]
    private static partial Regex UiKeyLiteral();
}
