using System.Text.RegularExpressions;
using Mmorpg.Testing;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Shared.Tests;

// The servers send a code and named values, the client gives a code to a failure it finds
// itself, and the client owns the sentence. These tests cover the text lookup, the source
// file of the messages, and the committed messages table.
public sealed partial class MessageTextTests
{
    [Fact]
    public void ACodeShowsItsTextAndACodeWithoutOneShowsTheCode()
    {
        var texts = Catalog(
            (TextKeys.UnlistedMessage(), "That did not work. [{code}]"),
            (TextKeys.Message("item_not_found"), "That item is no longer there."));

        Assert.Equal("That item is no longer there.", MessageText.Resolve(texts, "item_not_found"));
        Assert.True(MessageText.IsListed(texts, "item_not_found"));
        Assert.Equal("That did not work. [brand_new_code]", MessageText.Resolve(texts, "brand_new_code"));
        Assert.False(MessageText.IsListed(texts, "brand_new_code"));
    }

    // A code comes from the network, so one that cannot be part of a key is shown, not looked up.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Not A Code")]
    [InlineData("code/with/slashes")]
    public void ACodeThatCannotBeAKeyShowsTheUnlistedText(string? code)
    {
        var texts = Catalog((TextKeys.UnlistedMessage(), "That did not work. [{code}]"));

        Assert.Equal("That did not work. [" + code + "]", MessageText.Resolve(texts, code!));
        Assert.False(MessageText.IsListed(texts, code!));
    }

    [Fact]
    public void ArgumentsFillThePlaceholdersAsTextOrByTextKey()
    {
        var texts = Catalog(
            (TextKeys.UnlistedMessage(), "That did not work. [{code}]"),
            (TextKeys.Message("gathering_tool_required"), "{toolKind} required in the Tool slot, level {level}."),
            (TextKeys.ToolKindName("pickaxe"), "Pickaxe"));

        var text = MessageText.Resolve(
            texts,
            "gathering_tool_required",
            [
                MessageArgument.TextKey("toolKind", TextKeys.ToolKindName("pickaxe")),
                MessageArgument.Text("level", "12")
            ]);

        Assert.Equal("Pickaxe required in the Tool slot, level 12.", text);
    }

    // A missing value stays visible as its placeholder, and a text key without a text shows
    // the key, so neither failure hides behind an empty gap.
    [Fact]
    public void AMissingArgumentOrTextStaysVisible()
    {
        var texts = Catalog(
            (TextKeys.UnlistedMessage(), "That did not work. [{code}]"),
            (TextKeys.Message("gathering_tool_required"), "{toolKind} required in the Tool slot."));

        Assert.Equal(
            "{toolKind} required in the Tool slot.",
            MessageText.Resolve(texts, "gathering_tool_required"));
        Assert.Equal(
            "[tool-kind/hammer/name] required in the Tool slot.",
            MessageText.Resolve(
                texts,
                "gathering_tool_required",
                [MessageArgument.TextKey("toolKind", TextKeys.ToolKindName("hammer"))]));
    }

    [Theory]
    [InlineData("No placeholders.", new string[0])]
    [InlineData("{a} and {b2} and {a}.", new[] { "a", "b2" })]
    [InlineData("{toolKind}", new[] { "toolKind" })]
    public void PlaceholdersAreNamesInBraces(string template, string[] expected)
    {
        Assert.True(TextTemplate.TryReadPlaceholders(template, out var names));
        Assert.Equal(expected, names);
    }

    [Theory]
    [InlineData("An open {brace.")]
    [InlineData("A closing } brace.")]
    [InlineData("An {} empty name.")]
    [InlineData("A {tool-kind} with a dash.")]
    [InlineData("A {1st} digit first.")]
    [InlineData("A {nested {name}}.")]
    public void BracesThatAreNotPlaceholdersAreRefused(string template)
    {
        Assert.False(TextTemplate.TryReadPlaceholders(template, out _));
    }

    [Fact]
    public void MessageKeysAreBuiltFromCodes()
    {
        Assert.Equal("message/item_not_found", TextKeys.Message("item_not_found"));
        Assert.Equal("message-unlisted/text", TextKeys.UnlistedMessage());
        Assert.True(TextKeys.IsMessage("message/item_not_found"));
        Assert.False(TextKeys.IsMessage("message/item_not_found/extra"));
        Assert.False(TextKeys.IsMessage("item/weapon.iron_sword/name"));
        Assert.False(TextKeys.IsMessage(TextKeys.UnlistedMessage()));
        Assert.Throws<ArgumentException>(() => TextKeys.Message("Not A Code"));
    }

    [Fact]
    public void ATextSourceCompilesToItsTableSortedByKey()
    {
        var table = TextSource.Compile(Source(
            (TextKeys.UnlistedMessage(), "That did not work. [{code}]"),
            (TextKeys.Message("b_code"), "B."),
            (TextKeys.Message("a_code"), "A {value}.")));

        Assert.Equal(TextTableIds.Messages, table.TableId);
        Assert.Equal(TextTableFormat.SourceLocale, table.Locale);
        Assert.Equal(
            ["message-unlisted/text", "message/a_code", "message/b_code"],
            table.Entries.Select(entry => entry.Key));
        TextTable.Validate(table);
    }

    [Fact]
    public void ATextSourceIsRefusedWhenItBreaksItsRules()
    {
        var unlisted = (TextKeys.UnlistedMessage(), "That did not work. [{code}]");

        AssertRefused("for a code without a text is missing", Source((TextKeys.Message("a_code"), "A.")));
        AssertRefused("is not a message key", Source(unlisted, ("item/weapon.iron_sword/name", "Sword")));
        AssertRefused("uses braces", Source(unlisted, (TextKeys.Message("a_code"), "An open {brace.")));
        AssertRefused("needs a key and a text", Source(unlisted, (TextKeys.Message("a_code"), " ")));
        AssertRefused(
            "formatVersion",
            new TextSourceDocument { FormatVersion = 2, TableId = TextTableIds.Messages, Entries = [] });
        AssertRefused(
            "not a table that is written by hand",
            new TextSourceDocument
            {
                FormatVersion = TextSourceFormat.Version,
                TableId = TextTableIds.Items,
                Entries = []
            });
        Assert.Throws<TextTableValidationException>(() => TextSource.Compile(Source(
            unlisted,
            (TextKeys.Message("a_code"), "A."),
            (TextKeys.Message("a_code"), "A again."))));
    }

    // The committed table is what the source compiles to, so a hand edit of either is caught
    // here as well as by the verifier.
    [Fact]
    public void TheCommittedMessagesTableIsItsCompiledSource()
    {
        var source = StrictContentJson.DeserializeFile<TextSourceDocument>(
            ContentLayout.TextSourcePath(RepositoryPaths.Content, TextTableIds.Messages),
            "Text source");
        var committed = TextTableOutput.Deserialize(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(RepositoryPaths.Generated, "en", TextTableIds.Messages)));

        Assert.Equal(TextSource.Compile(source).Revision, committed.Revision);
    }

    // A text whose code neither a server sends nor the client gives a failure it finds itself
    // would never show, which is how a misspelled key hides. Every message key must be the
    // code of a string in the server, protocol, or client code.
    [Fact]
    public void EveryMessageBelongsToACodeThatAServerSendsOrTheClientMakes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in new[]
                 {
                     Path.Combine(RepositoryPaths.Root, "src", "Server"),
                     Path.Combine(RepositoryPaths.Root, "src", "Shared", "GameProtocol", "Runtime"),
                     Path.Combine(RepositoryPaths.Root, "src", "Shared", "WorldData", "Runtime"),
                     Path.Combine(RepositoryPaths.Root, "src", "Client", "Assets", "Scripts")
                 })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in CodeLiteral().Matches(File.ReadAllText(file)))
                {
                    codes.Add(match.Groups[1].Value);
                }
            }
        }

        var committed = TextTableOutput.Deserialize(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(RepositoryPaths.Generated, "en", TextTableIds.Messages)));
        var unknown = committed.Entries
            .Where(entry => TextKeys.IsMessage(entry.Key))
            .Select(entry => entry.Key[(entry.Key.IndexOf(TextKeys.Separator) + 1)..])
            .Where(code => !codes.Contains(code))
            .ToArray();

        Assert.True(unknown.Length == 0, "Neither a server nor the client makes these codes: " + string.Join(", ", unknown));
    }

    // The values that the servers send by name. A text that names another placeholder would
    // show it unfilled.
    [Fact]
    public void EveryPlaceholderOfTheMessagesIsAValueThatAServerSends()
    {
        var known = new[] { "capability", "toolKind" };
        var committed = TextTableOutput.Deserialize(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(RepositoryPaths.Generated, "en", TextTableIds.Messages)));

        foreach (var entry in committed.Entries)
        {
            Assert.True(TextTemplate.TryReadPlaceholders(entry.Text, out var names), entry.Key);
            var allowed = entry.Key == TextKeys.UnlistedMessage() ? [MessageText.CodeArgumentName] : known;
            Assert.True(
                names.All(name => allowed.Contains(name, StringComparer.Ordinal)),
                entry.Key + " names a placeholder that no server fills: " + string.Join(", ", names));
        }
    }

    private static void AssertRefused(string expected, TextSourceDocument source)
    {
        var errors = Assert.Throws<TextTableValidationException>(() => TextSource.Compile(source)).Errors;
        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    private static TextSourceDocument Source(params (string Key, string Text)[] entries)
    {
        return new TextSourceDocument
        {
            FormatVersion = TextSourceFormat.Version,
            TableId = TextTableIds.Messages,
            Entries = entries.Select(entry => new TextSourceEntry { Key = entry.Key, Text = entry.Text }).ToArray()
        };
    }

    private static TextCatalog Catalog(params (string Key, string Text)[] entries)
    {
        // One catalog may hold texts of several domains; a test table takes any valid key.
        return TextCatalog.Create(
        [
            TextTable.Create(
                TextTableIds.Messages,
                TextTableFormat.SourceLocale,
                entries.Select(entry => new TextTableEntry { Key = entry.Key, Text = entry.Text }))
        ]);
    }

    [GeneratedRegex("\"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\"")]
    private static partial Regex CodeLiteral();
}
