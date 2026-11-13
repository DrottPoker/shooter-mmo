using System;
using System.Collections.Generic;
using Mmorpg.Client.Api;

namespace Mmorpg.Client.Networking
{
    // Where the unexpected end of a joined session takes the player.
    public enum RealtimeEnding
    {
        // The line dropped: the World stays and the client joins it again by itself.
        Reconnect,

        // The server or the client decided: CharacterSelect with the text of the code, or LoginMenu
        // without an account session.
        CharacterSelect,

        // Another login took the account: LoginMenu with the text of the code.
        LoginMenu
    }

    // What the failure of one reconnect attempt means for the next.
    public enum ReconnectAttemptFailure
    {
        // The answer can get better by waiting, so another attempt follows within the window.
        Retry,

        // GameService no longer accepts the account session: the forced sign-out.
        SignOut,

        // A definite refusal, which waiting does not change: the client gives up at once.
        GiveUp
    }

    // The one place that says which endings reconnect (rule 1) and which do not (rule 2), and
    // which failures of an attempt are worth another attempt. Only a line that dropped without the
    // server asking for it reconnects; every code that a server sends, every protocol or state
    // failure that the client finds, a leave, and an abort end the World as before.
    public static class ReconnectRules
    {
        // The line dropped: the transport timed out or closed, the socket failed, or a join or
        // leave deadline passed. Only these, from the transport and the clock, reconnect.
        public static readonly IReadOnlyList<string> LineDroppedCodes = new[]
        {
            "connection_lost",
            "udp_socket_error",
            "realtime_timeout"
        };

        // The other network codes of the hub: a connection that a join could not open or that
        // closed before its join completed. They never end a joined session, and a reconnect
        // attempt that meets one tries again.
        public static readonly IReadOnlyList<string> ConnectFailureCodes = new[]
        {
            "connection_closed",
            "udp_start_failed",
            "udp_connect_failed"
        };

        // The worker could not reach GameService to accept the join, which can change.
        public static readonly IReadOnlyList<string> UnansweredJoinCodes = new[]
        {
            "auth_service_timeout",
            "auth_service_unavailable"
        };

        // GameService has no healthy worker for the shard or for the character's session right
        // now, which changes when the worker answers its heartbeat again.
        public static readonly IReadOnlyList<string> UnavailableTicketCodes = new[]
        {
            "shard_unavailable",
            "simulation_session_worker_unavailable"
        };

        public static RealtimeEnding Classify(RealtimeClientError error)
        {
            if (error == null)
            {
                return RealtimeEnding.CharacterSelect;
            }

            if (IsLineDropped(error))
            {
                return RealtimeEnding.Reconnect;
            }

            return string.Equals(error.Code, ClientSessionRecovery.AccountSessionReplacedCode, StringComparison.Ordinal)
                ? RealtimeEnding.LoginMenu
                : RealtimeEnding.CharacterSelect;
        }

        // A join that a reconnect attempt started and that did not complete.
        public static ReconnectAttemptFailure ClassifyAttempt(RealtimeClientError error)
        {
            if (error == null)
            {
                return ReconnectAttemptFailure.GiveUp;
            }

            if (IsLineDropped(error)
                || (error.Kind == RealtimeClientErrorKind.Network && Contains(ConnectFailureCodes, error.Code))
                || (error.Kind == RealtimeClientErrorKind.Server && Contains(UnansweredJoinCodes, error.Code)))
            {
                return ReconnectAttemptFailure.Retry;
            }

            return ReconnectAttemptFailure.GiveUp;
        }

        // The join ticket request of a reconnect attempt that GameService did not grant.
        public static ReconnectAttemptFailure ClassifyAttempt(ApiError error)
        {
            if (error == null)
            {
                return ReconnectAttemptFailure.GiveUp;
            }

            if (error.IsUnauthorized)
            {
                return ReconnectAttemptFailure.SignOut;
            }

            if (error.Kind != ApiErrorKind.Http
                || error.StatusCode >= 500
                || Contains(UnavailableTicketCodes, error.Code))
            {
                return ReconnectAttemptFailure.Retry;
            }

            return ReconnectAttemptFailure.GiveUp;
        }

        // Only the transport and the clock report a dropped line; a server that sends one of these
        // codes decided to end the connection.
        private static bool IsLineDropped(RealtimeClientError error)
        {
            return (error.Kind == RealtimeClientErrorKind.Network || error.Kind == RealtimeClientErrorKind.Timeout)
                && Contains(LineDroppedCodes, error.Code);
        }

        private static bool Contains(IReadOnlyList<string> codes, string code)
        {
            for (var index = 0; index < codes.Count; index++)
            {
                if (string.Equals(codes[index], code, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
