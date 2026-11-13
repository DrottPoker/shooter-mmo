using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Api;
using Mmorpg.Client.Networking;
using Mmorpg.WorldData.Editor;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Which ends of a joined session reconnect, where every other end takes the player, and which
    // failures of a reconnect attempt are worth another one. The tables name every code that the
    // hub makes with a kind of its own; a new one fails until it has a row. Every protocol failure
    // that the client finds and every code that the worker ends a connection with are read from
    // the sources and checked too.
    public sealed class ReconnectRulesTests
    {
        private const RealtimeClientErrorKind Network = RealtimeClientErrorKind.Network;
        private const RealtimeClientErrorKind Timeout = RealtimeClientErrorKind.Timeout;
        private const RealtimeClientErrorKind Validation = RealtimeClientErrorKind.Validation;
        private const RealtimeClientErrorKind Busy = RealtimeClientErrorKind.Busy;
        private const RealtimeClientErrorKind Protocol = RealtimeClientErrorKind.Protocol;
        private const RealtimeClientErrorKind Server = RealtimeClientErrorKind.Server;

        // A code that the hub or a feature gives a failure with a literal kind.
        private static readonly Regex KindAndCode = new Regex(
            "RealtimeClientErrorKind\\.(\\w+),\\s*\"([a-z][a-z0-9_]*)\"",
            RegexOptions.Compiled);

        // Protocol failures that the client finds: the hub's own, a feature's, and a message in a
        // state or on a channel that the table refuses.
        private static readonly Regex[] ProtocolCodes =
        {
            new Regex("FailProtocol\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("DisconnectForClientFailure\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("\"(unexpected_[a-z0-9_]+)\"", RegexOptions.Compiled),
            new Regex("\"(invalid_[a-z0-9_]+_delivery)\"", RegexOptions.Compiled)
        };

        // The codes that the worker ends a connection or refuses a join with.
        private static readonly Regex[] ServerCodes =
        {
            new Regex("RejectProtocol\\([^;]*?\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled | RegexOptions.Singleline),
            new Regex("EncodeServerDisconnect\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("sessionStore\\.Invalidate\\([^;]*?\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled | RegexOptions.Singleline),
            new Regex("SimulationSessionInvalidation\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("public const string \\w+Code = \"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled)
        };

        // Rule 1 and 2 for a joined session that ended without a leave or an abort.
        private static readonly (RealtimeClientErrorKind Kind, string Code, RealtimeEnding Ending)[] Endings =
        {
            // The line dropped: the transport, the socket, and a deadline.
            (Network, "connection_lost", RealtimeEnding.Reconnect),
            (Network, "udp_socket_error", RealtimeEnding.Reconnect),
            (Timeout, "realtime_timeout", RealtimeEnding.Reconnect),

            // The rest that the hub makes never ends a joined session, and none is a dropped line.
            (Network, "connection_closed", RealtimeEnding.CharacterSelect),
            (Network, "udp_start_failed", RealtimeEnding.CharacterSelect),
            (Network, "udp_connect_failed", RealtimeEnding.CharacterSelect),
            (Validation, "invalid_join_parameters", RealtimeEnding.CharacterSelect),
            (Validation, "join_ticket_expired", RealtimeEnding.CharacterSelect),
            (Validation, "incompatible_protocol", RealtimeEnding.CharacterSelect),
            (Validation, "incompatible_simulation", RealtimeEnding.CharacterSelect),
            (Validation, "not_in_simulation", RealtimeEnding.CharacterSelect),
            (Validation, "missing_simulation_session", RealtimeEnding.CharacterSelect),
            (Busy, "realtime_busy", RealtimeEnding.CharacterSelect),

            // A server that sends a code of a dropped line still decided to end it.
            (Server, "connection_lost", RealtimeEnding.CharacterSelect),
            (Server, "realtime_timeout", RealtimeEnding.CharacterSelect),

            // Every code the worker sends, among them these, ends the World.
            (Server, "account_session_replaced", RealtimeEnding.LoginMenu),
            (Server, "session_revoked", RealtimeEnding.CharacterSelect),
            (Server, "session_reconnected", RealtimeEnding.CharacterSelect),
            (Server, "session_expired", RealtimeEnding.CharacterSelect),
            (Server, "session_missing", RealtimeEnding.CharacterSelect),
            (Server, "udp_rate_limited", RealtimeEnding.CharacterSelect),
            (Server, "invalid_packet", RealtimeEnding.CharacterSelect),
            (Server, "internal_error", RealtimeEnding.CharacterSelect),
            (Server, "carry_state_conflict", RealtimeEnding.CharacterSelect),
            (Server, "worker_runtime_changed", RealtimeEnding.CharacterSelect),

            // And so does every protocol or state failure that the client finds.
            (Protocol, "invalid_join_response", RealtimeEnding.CharacterSelect),
            (Protocol, "placement_mismatch", RealtimeEnding.CharacterSelect),
            (Protocol, "collision_stream_failed", RealtimeEnding.CharacterSelect),
            (Protocol, "account_session_replaced", RealtimeEnding.LoginMenu)
        };

        // What the failure of an attempt's join means for the next attempt.
        private static readonly (RealtimeClientErrorKind Kind, string Code, ReconnectAttemptFailure Failure)[] JoinFailures =
        {
            (Network, "connection_lost", ReconnectAttemptFailure.Retry),
            (Network, "udp_socket_error", ReconnectAttemptFailure.Retry),
            (Timeout, "realtime_timeout", ReconnectAttemptFailure.Retry),
            (Network, "connection_closed", ReconnectAttemptFailure.Retry),
            (Network, "udp_start_failed", ReconnectAttemptFailure.Retry),
            (Network, "udp_connect_failed", ReconnectAttemptFailure.Retry),
            (Server, "auth_service_timeout", ReconnectAttemptFailure.Retry),
            (Server, "auth_service_unavailable", ReconnectAttemptFailure.Retry),
            (Server, "invalid_ticket", ReconnectAttemptFailure.GiveUp),
            (Server, "wrong_simulation_worker", ReconnectAttemptFailure.GiveUp),
            (Server, "account_character_already_active", ReconnectAttemptFailure.GiveUp),
            (Server, "character_already_active", ReconnectAttemptFailure.GiveUp),
            (Server, "simulation_session_changed", ReconnectAttemptFailure.GiveUp),
            (Server, "join_failed", ReconnectAttemptFailure.GiveUp),
            (Server, "join_timeout", ReconnectAttemptFailure.GiveUp),
            (Server, "connection_lost", ReconnectAttemptFailure.GiveUp),
            (Validation, "join_ticket_expired", ReconnectAttemptFailure.GiveUp),
            (Validation, "incompatible_protocol", ReconnectAttemptFailure.GiveUp),
            (Validation, "incompatible_simulation", ReconnectAttemptFailure.GiveUp),
            (Validation, "invalid_join_parameters", ReconnectAttemptFailure.GiveUp),
            (Busy, "realtime_busy", ReconnectAttemptFailure.GiveUp),
            (Protocol, "invalid_join_response", ReconnectAttemptFailure.GiveUp),
            (Protocol, "placement_mismatch", ReconnectAttemptFailure.GiveUp)
        };

        // What GameService's answer to an attempt's ticket request means.
        private static readonly (ApiErrorKind Kind, long Status, string Code, ReconnectAttemptFailure Failure)[] TicketFailures =
        {
            (ApiErrorKind.Network, 0, "network_error", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Timeout, 0, "request_timeout", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.InvalidResponse, 0, "invalid_response", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Http, 500, "http_error", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Http, 503, "auth_service_unavailable", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Http, 409, "shard_unavailable", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Http, 409, "simulation_session_worker_unavailable", ReconnectAttemptFailure.Retry),
            (ApiErrorKind.Http, 401, "invalid_session_token", ReconnectAttemptFailure.SignOut),
            (ApiErrorKind.Http, 401, "account_session_replaced", ReconnectAttemptFailure.SignOut),
            (ApiErrorKind.Http, 409, "account_character_already_active", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 409, "character_already_active", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 409, "simulation_worker_item_catalog_mismatch", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 409, "simulation_worker_quest_catalog_mismatch", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 404, "character_not_found", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 404, "shard_not_found", ReconnectAttemptFailure.GiveUp),
            (ApiErrorKind.Http, 400, "invalid_shard_id", ReconnectAttemptFailure.GiveUp)
        };

        [TestCaseSource(nameof(EndingCases))]
        public void AnEndingLandsWhereItsTableRowSays(RealtimeClientErrorKind kind, string code, RealtimeEnding ending)
        {
            Assert.That(ReconnectRules.Classify(Error(kind, code)), Is.EqualTo(ending));
        }

        [TestCaseSource(nameof(JoinFailureCases))]
        public void AFailedJoinOfAnAttemptIsRetriedOrEndsAsItsTableRowSays(
            RealtimeClientErrorKind kind,
            string code,
            ReconnectAttemptFailure failure)
        {
            Assert.That(ReconnectRules.ClassifyAttempt(Error(kind, code)), Is.EqualTo(failure));
        }

        [TestCaseSource(nameof(TicketFailureCases))]
        public void ARefusedTicketOfAnAttemptIsRetriedOrEndsAsItsTableRowSays(
            ApiErrorKind kind,
            long status,
            string code,
            ReconnectAttemptFailure failure)
        {
            Assert.That(
                ReconnectRules.ClassifyAttempt(new ApiError(kind, status, code, "Diagnostic.", string.Empty)),
                Is.EqualTo(failure));
        }

        // The codes of a dropped line are the only network and timeout codes that reconnect, and
        // every network or timeout code of the client has a place in the rules.
        [Test]
        public void EveryNetworkCodeOfTheClientIsADroppedLineOrAFailedConnect()
        {
            var made = MadeByTheClient().Where(entry => entry.Kind == Network || entry.Kind == Timeout).ToArray();

            Assert.That(made.Select(entry => entry.Code), Is.SupersetOf(ReconnectRules.LineDroppedCodes));
            Assert.That(
                made.Select(entry => entry.Code).Where(code =>
                    !ReconnectRules.LineDroppedCodes.Contains(code) && !ReconnectRules.ConnectFailureCodes.Contains(code)),
                Is.Empty);
        }

        // A new code that the client makes with a kind of its own needs its row in the table.
        [Test]
        public void EveryCodeTheClientMakesWithAKindHasARow()
        {
            var rows = new HashSet<(RealtimeClientErrorKind, string)>(Endings.Select(row => (row.Kind, row.Code)));

            Assert.That(
                MadeByTheClient().Where(entry => !rows.Contains(entry)).Select(entry => entry.Kind + " " + entry.Code),
                Is.Empty);
        }

        [Test]
        public void EveryProtocolFailureOfTheClientEndsAtCharacterSelect()
        {
            var codes = CodesIn(ClientScripts, ProtocolCodes).Distinct().ToArray();

            Assert.That(codes, Has.Length.GreaterThan(40), "The sources name the client's protocol failures.");
            Assert.That(
                codes.Where(code => ReconnectRules.Classify(Error(Protocol, code)) != RealtimeEnding.CharacterSelect),
                Is.Empty);
        }

        // Rule 2: no code that the worker sends reconnects. A new login of the account goes to
        // LoginMenu; every other code to CharacterSelect.
        [Test]
        public void NoCodeOfTheWorkerReconnects()
        {
            var codes = CodesIn(Path.Combine(RepositoryLayout.FindRoot(), "src", "Server", "SimulationWorker"), ServerCodes)
                .Distinct()
                .ToArray();

            Assert.That(codes, Has.Length.GreaterThan(10), "The sources name the worker's disconnect codes.");
            Assert.That(codes, Does.Contain("session_reconnected").And.Contain("account_session_replaced").And.Contain("udp_rate_limited"));
            foreach (var code in codes)
            {
                Assert.That(
                    ReconnectRules.Classify(Error(Server, code)),
                    Is.EqualTo(code == "account_session_replaced" ? RealtimeEnding.LoginMenu : RealtimeEnding.CharacterSelect),
                    code);
            }
        }

        private static string ClientScripts
        {
            get { return Path.Combine(RepositoryLayout.FindRoot(), "src", "Client", "Assets", "Scripts"); }
        }

        private static IEnumerable<TestCaseData> EndingCases()
        {
            return Endings.Select(row => new TestCaseData(row.Kind, row.Code, row.Ending));
        }

        private static IEnumerable<TestCaseData> JoinFailureCases()
        {
            return JoinFailures.Select(row => new TestCaseData(row.Kind, row.Code, row.Failure));
        }

        private static IEnumerable<TestCaseData> TicketFailureCases()
        {
            return TicketFailures.Select(row => new TestCaseData(row.Kind, row.Status, row.Code, row.Failure));
        }

        private static RealtimeClientError Error(RealtimeClientErrorKind kind, string code)
        {
            return new RealtimeClientError(kind, code, "Diagnostic.");
        }

        private static (RealtimeClientErrorKind Kind, string Code)[] MadeByTheClient()
        {
            var made = new HashSet<(RealtimeClientErrorKind, string)>();
            foreach (var path in Directory.GetFiles(ClientScripts, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in KindAndCode.Matches(File.ReadAllText(path)))
                {
                    made.Add(((RealtimeClientErrorKind)System.Enum.Parse(typeof(RealtimeClientErrorKind), match.Groups[1].Value), match.Groups[2].Value));
                }
            }

            Assert.That(made, Has.Count.GreaterThan(10), "The sources name the client's own codes.");
            return made.ToArray();
        }

        private static IEnumerable<string> CodesIn(string directory, Regex[] patterns)
        {
            foreach (var path in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path);
                foreach (var pattern in patterns)
                {
                    foreach (Match match in pattern.Matches(text))
                    {
                        yield return match.Groups[1].Value;
                    }
                }
            }
        }
    }
}
