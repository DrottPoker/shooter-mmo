using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Accounts;

// The sliding end of an account session: the earlier of the last activity plus the idle lifetime
// and the login plus the maximum lifetime. Activity moves it forward at most once an hour, and
// only while the session is active, so an extension never revives an ended session, which
// retention may already have deleted.
internal sealed class AccountSessionActivity(
    AccountsModuleOptions options,
    IAccountSessionReferences references) : IAccountSessionActivity
{
    // Activity writes the end only when it moves by more than this, so a busy session costs at
    // most one write an hour.
    public static readonly TimeSpan ExtensionStep = TimeSpan.FromHours(1);

    // The end that activity now gives the account session row of the alias.
    public static string EndSql(string sessionAlias)
    {
        return $"""
            least(
                now() + make_interval(secs => @SessionIdleLifetimeSeconds),
                {sessionAlias}.created_at + make_interval(secs => @SessionMaximumLifetimeSeconds))
            """;
    }

    // The condition, in the statement that writes, under which activity moves the end of the
    // account session row of the alias: the session is active and its end moves by more than an
    // hour. A session that this statement waited for is checked again in its newest version.
    public static string ExtendsSql(string sessionAlias, IAccountSessionReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return $"""
            {AccountsSql.IsActiveSession(sessionAlias, references.IsHeld)}
              and {EndSql(sessionAlias)} > {sessionAlias}.expires_at + make_interval(secs => @SessionExtensionStepSeconds)
            """;
    }

    // The parameters of EndSql and ExtendsSql, beside the statement's own values.
    public static DynamicParameters EndParameters(AccountsModuleOptions options, object values)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parameters = new DynamicParameters(values);
        parameters.Add("SessionIdleLifetimeSeconds", options.SessionIdleLifetime.TotalSeconds);
        parameters.Add("SessionMaximumLifetimeSeconds", options.SessionMaximumLifetime.TotalSeconds);
        parameters.Add("SessionExtensionStepSeconds", ExtensionStep.TotalSeconds);
        return parameters;
    }

    public async Task<int> ExtendAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> accountSessionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(accountSessionIds);
        if (accountSessionIds.Count == 0)
        {
            return 0;
        }

        // Every statement that locks several account sessions locks them in ascending id order.
        var sql = $"""
            with extended_session as materialized (
                select account_session.id
                from account_sessions account_session
                where account_session.id = any(@AccountSessionIds)
                  and {ExtendsSql("account_session", references)}
                order by account_session.id
                for no key update of account_session
            )
            update account_sessions account_session
            set expires_at = {EndSql("account_session")}
            from extended_session
            where account_session.id = extended_session.id;
            """;
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            EndParameters(options, new { AccountSessionIds = accountSessionIds.Distinct().ToArray() }),
            cancellationToken: cancellationToken));
    }
}
