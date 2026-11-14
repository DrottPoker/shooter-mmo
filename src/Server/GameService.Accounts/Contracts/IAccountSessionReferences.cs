namespace Mmorpg.GameService.Accounts.Contracts;

// Lets a higher module say which account sessions its rows still name, so the retention of account
// sessions never deletes one of them: their foreign keys would set the name to null, and a
// simulation session or join ticket without an account session counts as unbound, not ended.
// Exactly one implementation is registered.
public interface IAccountSessionReferences
{
    // A SQL condition that is true while no row names the account session of the trusted SQL
    // expression. It uses its own aliases, as the owners' SQL fragments do, and only not exists
    // conditions joined by and, so the planner can look up each name with an index.
    string IsUnreferenced(string accountSessionIdExpression);
}
