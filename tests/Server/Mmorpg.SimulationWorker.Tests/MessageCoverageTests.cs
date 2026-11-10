using System.Reflection;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

namespace Mmorpg.SimulationWorker.Tests;

// The worker sends codes and the client owns the sentences. A code that a player meets in
// normal play must have a text of its own; a code without one shows only the code.
public sealed class MessageCoverageTests
{
    // Codes that a correct client never shows: its own close needs no text, and a malformed
    // intent is a defect, which the text for an unlisted code reports with the code.
    private static readonly string[] WithoutOwnText =
    [
        RealtimeCorpseViewClosureCodes.ViewClosed,
        RealtimeCombatErrorCodes.IntentInvalid
    ];

    [Theory]
    [InlineData(typeof(WorldInteractionNoticeCodes))]
    [InlineData(typeof(GatheringStopCodes))]
    [InlineData(typeof(RealtimeCombatErrorCodes))]
    [InlineData(typeof(RealtimeCorpseViewClosureCodes))]
    public void EveryCodeOfPlayHasATextInTheMessagesTable(Type codes)
    {
        var keys = MessageKeys();
        var missing = codes
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(code => !WithoutOwnText.Contains(code, StringComparer.Ordinal))
            .Where(code => !keys.Contains(TextKeys.Message(code)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "content/text/messages.json has no text for: " + string.Join(", ", missing));
    }

    private static HashSet<string> MessageKeys()
    {
        using var table = JsonDocument.Parse(File.ReadAllText(
            ContentLayout.GeneratedTextTablePath(RepositoryPaths.Generated, "en", TextTableIds.Messages)));
        return table.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
