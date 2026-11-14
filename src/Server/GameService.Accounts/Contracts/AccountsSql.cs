namespace Mmorpg.GameService.Accounts.Contracts;

// Read-only SQL fragments over Accounts tables for other modules' statements. Each
// argument is a trusted SQL expression from the caller's own statement, never a user
// value. The fragments use their own aliases so they never collide with the caller's.
public static class AccountsSql
{
    // The one rule of whether an account session is active, over the account session row with
    // the given alias: it is not revoked, and its end has not passed or a live simulation session
    // holds it, so a player in the World is never logged out by the clock. isHeld builds the
    // condition of IAccountSessionReferences.IsHeld for an account session id expression. A
    // logout, a revoke, and a new login end a session at once, also a held one.
    public static string IsActiveSession(string sessionAlias, Func<string, string> isHeld)
    {
        ArgumentNullException.ThrowIfNull(isHeld);
        return $"""
            ({sessionAlias}.revoked_at is null
                and ({sessionAlias}.expires_at > now() or {isHeld($"{sessionAlias}.id")}))
            """;
    }

    // True when the account session expression is null or names a session that is active.
    public static string IsActiveOrUnboundSession(string accountSessionIdExpression, Func<string, string> isHeld)
    {
        return $"""
            ({accountSessionIdExpression} is null
                or exists (
                    select 1
                    from account_sessions accounts_sql_session
                    where accounts_sql_session.id = {accountSessionIdExpression}
                      and {IsActiveSession("accounts_sql_session", isHeld)}))
            """;
    }

    // The session's revocation reason, or null when it is active or does not exist.
    public static string RevocationReasonOf(string accountSessionIdExpression)
    {
        return $"""
            (select accounts_sql_session.revocation_reason
             from account_sessions accounts_sql_session
             where accounts_sql_session.id = {accountSessionIdExpression})
            """;
    }

    // The account id when the account exists, otherwise null.
    public static string ExistingIdOrNull(string accountIdExpression)
    {
        return $"""
            (select accounts_sql_account.id
             from accounts accounts_sql_account
             where accounts_sql_account.id = {accountIdExpression})
            """;
    }

    public static string UsernameOf(string accountIdExpression)
    {
        return $"""
            (select accounts_sql_account.username
             from accounts accounts_sql_account
             where accounts_sql_account.id = {accountIdExpression})
            """;
    }
}
