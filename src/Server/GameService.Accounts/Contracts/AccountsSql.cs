namespace Mmorpg.GameService.Accounts.Contracts;

// Read-only SQL fragments over Accounts tables for other modules' statements. Each
// argument is a trusted SQL expression from the caller's own statement, never a user
// value. The fragments use their own aliases so they never collide with the caller's.
public static class AccountsSql
{
    // True when the account session expression is null or names a session that is
    // neither revoked nor expired.
    public static string IsActiveOrUnboundSession(string accountSessionIdExpression)
    {
        return $"""
            ({accountSessionIdExpression} is null
                or exists (
                    select 1
                    from account_sessions accounts_sql_session
                    where accounts_sql_session.id = {accountSessionIdExpression}
                      and accounts_sql_session.revoked_at is null
                      and accounts_sql_session.expires_at > now()))
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
