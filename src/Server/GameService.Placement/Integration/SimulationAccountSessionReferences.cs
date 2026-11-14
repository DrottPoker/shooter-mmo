using Mmorpg.GameService.Accounts.Contracts;

namespace Mmorpg.GameService.Placement.Integration;

// The simulation sessions and join tickets that name an account session, live or ended, until
// Placement's own retention deleted them, and the live simulation sessions that hold one. Every
// lookup uses the account session indexes.
internal sealed class SimulationAccountSessionReferences : IAccountSessionReferences
{
    public string IsUnreferenced(string accountSessionIdExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountSessionIdExpression);
        return $"""
            (not exists (
                 select 1
                 from character_simulation_sessions placement_reference_session
                 where placement_reference_session.account_session_id = {accountSessionIdExpression})
             and not exists (
                 select 1
                 from simulation_join_tickets placement_reference_ticket
                 where placement_reference_ticket.account_session_id = {accountSessionIdExpression}))
            """;
    }

    public string IsHeld(string accountSessionIdExpression)
    {
        return Held(accountSessionIdExpression);
    }

    // A player in the World, or the body of one who just left it, holds the account session of
    // its simulation session while that session is live.
    public static string Held(string accountSessionIdExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountSessionIdExpression);
        return $"""
            exists (
                select 1
                from character_simulation_sessions placement_held_session
                where placement_held_session.account_session_id = {accountSessionIdExpression}
                  and placement_held_session.released_at is null
                  and placement_held_session.expires_at > now())
            """;
    }

    // AccountsSql's rule of an active account session, with the live simulation sessions that
    // hold one; every Placement statement asks through it.
    public static string IsActiveOrUnbound(string accountSessionIdExpression)
    {
        return AccountsSql.IsActiveOrUnboundSession(accountSessionIdExpression, Held);
    }
}
