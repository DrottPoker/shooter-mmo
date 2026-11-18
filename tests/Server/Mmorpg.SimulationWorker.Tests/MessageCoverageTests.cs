using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

namespace Mmorpg.SimulationWorker.Tests;

// The worker sends codes and the client owns the sentences. A code that a player meets in
// normal play must have a text of its own; a code without one shows only the code.
public sealed partial class MessageCoverageTests
{
    // Codes that a correct client never shows: its own close needs no text, and a malformed
    // intent is a defect, which the text for an unlisted code reports with the code.
    private static readonly string[] WithoutOwnText =
    [
        RealtimeCorpseViewClosureCodes.ViewClosed,
        RealtimeCombatErrorCodes.IntentInvalid
    ];

    // The projects whose codes reach a player: the HTTP API that the client calls and the worker
    // that talks to it over UDP. The Items, Economy, and Quests transaction codes are not scanned;
    // their lists are not part of this guard.
    private static readonly string[] ServerProjects =
    [
        "Common",
        "GameService",
        "GameService.Accounts",
        "GameService.Characters",
        "GameService.Content",
        "GameService.Placement",
        "GameService.Platform",
        "SimulationWorker"
    ];

    // The codes of those projects that no player meets in normal play, each with the reason. A code
    // is here, or it has a text; a code that has both, or that no scanned code makes any more, fails
    // the guard, so this list cannot rot.
    private static readonly Dictionary<string, string> ServerCodesWithoutText = Build(
        (
            "A peer that has not joined, or one without the feature, sends an intent: a defect of the client",
            [
                "admin_unavailable", "appearance_unavailable", "combat_unavailable",
                "corpse_interaction_unavailable", "item_operation_unavailable",
                "movement_unavailable", "quest_intent_unavailable", "world_interaction_unavailable"
            ]
        ),
        (
            "A malformed request, packet, or intent: a defect of the client or a tampered one, which shows the unlisted text with its code",
            [
                "invalid_admin_intent", "invalid_appearance_intent", "invalid_character_id",
                "invalid_corpse_operation", "invalid_item_operation", "invalid_join_request",
                "invalid_movement_input", "invalid_quest_intent", "invalid_request",
                "invalid_shard_id", "invalid_simulation_target", "invalid_world_interaction",
                "missing_join_ticket", "unknown_movement_path"
            ]
        ),
        (
            "The lease of an interaction that the worker itself keeps, or its authority checks, which end in a closed view or in the disconnect that names the session",
            [
                "corpse_interaction_invalid", "world_interaction_session_invalid"
            ]
        ),
        (
            "Between the worker and GameService only: a worker is refused, or it refuses its own work, and no player is told",
            [
                "invalid_character_checkpoint", "invalid_character_state", "invalid_service_credentials",
                "invalid_simulation_session", "invalid_simulation_session_heartbeat",
                "invalid_simulation_session_id", "invalid_simulation_session_request",
                "invalid_simulation_session_token", "invalid_simulation_worker_identity",
                "invalid_worker_runtime", "mob_corpse_authority_invalid", "mob_corpse_creation_failed",
                "mob_corpse_empty", "mob_corpse_persistence_invalid", "mob_corpse_source_invalid",
                "service_contract_mismatch", "service_worker_mismatch", "shard_assignment_conflict",
                "shard_world_rebind_blocked", "simulation_assignment_target_not_found",
                "simulation_topology_mismatch", "simulation_world_not_found", "simulation_worker_not_found"
            ]
        ),
        (
            "The Development environment only: bots and the look tool, which production does not serve",
            [
                "development_bot_already_active", "development_bot_identity_changed",
                "development_bot_loopback_required", "development_bot_placement_unavailable",
                "development_bot_worker_capacity_reached", "development_look_disabled",
                "development_simulation_bots_disabled", "invalid_appearance_change",
                "invalid_development_bot_authority", "invalid_development_bot_index",
                "invalid_development_bot_instance_id", "invalid_join_ticket"
            ]
        ),
        (
            "The client asks only for the content revisions that its manifest names; a changed revision shows the update texts of the catalogs",
            [
                "content_not_found"
            ]
        ));

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

    // Every code that the HTTP API and the worker make in the scanned projects has a text or a
    // reason that it needs none. The scan sees a code where the source writes it as a literal in one
    // of these places: an argument of a call that builds an error, a constant named for a code, or
    // the code member of Problem Details. It cannot see a code that arrives through a variable, one
    // that is composed at run time, one of the Items, Economy, or Quests transaction code lists, or
    // one outside the scanned projects; those need their own list, as the theory above has for the
    // worker's notices.
    [Fact]
    public void EveryServerCodeThatAPlayerCanMeetHasATextOrAReason()
    {
        var keys = MessageKeys();
        var codes = ScanServerCodes();

        var undecided = codes.Keys
            .Where(code => !keys.Contains(TextKeys.Message(code)))
            .Where(code => !ServerCodesWithoutText.ContainsKey(code))
            .Order(StringComparer.Ordinal)
            .Select(code => code + " (" + string.Join(", ", codes[code].Order(StringComparer.Ordinal).Take(2)) + ")")
            .ToArray();
        Assert.True(
            undecided.Length == 0,
            "Give these codes a text in content/text/messages.json, or add them with a reason to ServerCodesWithoutText: "
                + string.Join("; ", undecided));

        var stale = ServerCodesWithoutText.Keys
            .Where(code => !codes.ContainsKey(code) || keys.Contains(TextKeys.Message(code)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            stale.Length == 0,
            "These codes are listed without a text, but a text exists or no server makes them any more: "
                + string.Join(", ", stale));
    }

    // The codes that end a player's session or the connection are shown to the player as the text of
    // the code itself, so each needs one, whichever project makes it. The codes are found by asking
    // GameServiceOutcome, which decides what ends a session, about every code literal of the server.
    [Fact]
    public void EveryCodeThatEndsASessionHasAText()
    {
        var keys = MessageKeys();
        var serverRoot = Path.Combine(RepositoryPaths.Root, "src", "Server");
        var ending = EnumerateSources(serverRoot)
            .SelectMany(file => CodeLiteral().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(GameServiceOutcome.EndsItemSession)
            .ToArray();

        Assert.NotEmpty(ending);
        var missing = ending.Where(code => !keys.Contains(TextKeys.Message(code))).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "These codes end a session and have no text in content/text/messages.json: " + string.Join(", ", missing));
    }

    private static Dictionary<string, HashSet<string>> ScanServerCodes()
    {
        var codes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in ServerProjects)
        {
            var directory = Path.Combine(RepositoryPaths.Root, "src", "Server", project);
            foreach (var file in EnumerateSources(directory))
            {
                var text = File.ReadAllText(file);
                foreach (var position in CodePositions)
                {
                    foreach (Match match in position.Matches(text))
                    {
                        foreach (var group in match.Groups.Cast<Group>().Skip(1).Where(group => group.Success))
                        {
                            if (!codes.TryGetValue(group.Value, out var files))
                            {
                                files = new HashSet<string>(StringComparer.Ordinal);
                                codes[group.Value] = files;
                            }

                            files.Add(project + "/" + Path.GetFileName(file));
                        }
                    }
                }
            }
        }

        Assert.True(codes.Count > 60, "The scan found only " + codes.Count + " codes, so it no longer sees the sources.");
        return codes;
    }

    private static IEnumerable<string> EnumerateSources(string directory)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal));
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

    private static Dictionary<string, string> Build(params (string Reason, string[] Codes)[] groups)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (reason, codes) in groups)
        {
            foreach (var code in codes)
            {
                result.Add(code, reason);
            }
        }

        return result;
    }

    private const string CodePattern = "\"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\"";

    // Where a source writes a code that is sent out: a call that builds an error or a rejection,
    // an error type built in place, a constant whose name ends in Code or Codes, the code member of
    // Problem Details or of an anonymous answer, and the two branches of a conditional.
    private static readonly Regex[] CodePositions =
    [
        new(
            @"\.(?:BadRequest|Unauthorized|Forbidden|NotFound|Conflict|UnprocessableEntity|ServiceUnavailable|Failure|Reject|RejectProtocol|Problem|Fail)\s*\((?:[^""()]|\([^()]*\))*?,?\s*" + CodePattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(
            @"(?:new\s+(?:ErrorResponse|SimulationWorkerErrorResponse|SimulationJoinResult\w*)\s*\(|ServiceResult<[^>]*>\.\w+\()\s*" + CodePattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"const\s+string\s+\w*(?:Code|Codes)\s*=\s*" + CodePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\[""code""\]\s*=\s*(?:[^""?;]*\?\s*)?" + CodePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bcode\s*=\s*" + CodePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\?\s*" + CodePattern + @"\s*:\s*" + CodePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled)
    ];

    [GeneratedRegex(CodePattern)]
    private static partial Regex CodeLiteral();
}
