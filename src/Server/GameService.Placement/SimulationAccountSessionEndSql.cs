using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;

namespace Mmorpg.GameService.Placement;

// How the account session of a simulation session ended, as a SQL expression over the session row
// with the given alias: null while it has none or it is still valid and the session carries no
// mark, otherwise a SimulationAccountSessionEnd value. A session counts as ended once it carries
// the mark or its account session is revoked or expired, so an expiry counts before the heartbeat
// marks it.
internal static class SimulationAccountSessionEndSql
{
    public static string Of(string sessionAlias)
    {
        return $"""
            (case
                when {sessionAlias}.account_session_ended_at is null
                     and {AccountsSql.IsActiveOrUnboundSession($"{sessionAlias}.account_session_id")} then null
                when {AccountsSql.RevocationReasonOf($"{sessionAlias}.account_session_id")}
                     = '{AccountSessionRevocationReason.SessionReplaced}' then {(int)SimulationAccountSessionEnd.Replaced}
                else {(int)SimulationAccountSessionEnd.Ended}
            end)
            """;
    }
}
