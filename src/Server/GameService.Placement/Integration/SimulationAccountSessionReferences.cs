using Mmorpg.GameService.Accounts.Contracts;

namespace Mmorpg.GameService.Placement.Integration;

// The simulation sessions and join tickets that name an account session, live or ended, until
// Placement's own retention deleted them. Both lookups use the account session indexes.
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
}
