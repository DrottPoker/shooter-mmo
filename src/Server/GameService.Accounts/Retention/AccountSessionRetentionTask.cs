using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;

namespace Mmorpg.GameService.Accounts.Retention;

// Account sessions, counted from the expiry of their token, also when they were revoked earlier:
// until then a replaced token still answers account_session_replaced, and the session cache keeps
// no entry past it. An active session is never due, and a session that a simulation session or a
// join ticket still names waits until Placement's retention deleted those. No new row can name a
// session whose token expired, so one statement may lock and decide.
internal sealed class AccountSessionRetentionTask(IAccountSessionReferences references) : IRetentionTask
{
    public string Name => "account_sessions";

    public RetentionClass Class => RetentionClass.SessionHistory;

    public async Task<RetentionBatchResult> DeleteDueAsync(
        PostgresTransactionScope scope,
        RetentionBatch batch,
        CancellationToken cancellationToken)
    {
        var row = await scope.Connection.QuerySingleAsync<RetentionBatchRow>(new CommandDefinition(
            $"""
            with due as (
                select account_session.id, account_session.expires_at
                from account_sessions account_session
                where account_session.expires_at < now() - make_interval(secs => @WindowSeconds)
                  and {references.IsUnreferenced("account_session.id")}
                order by account_session.expires_at, account_session.id
                limit @BatchSize
                for update of account_session skip locked
            ), deleted as (
                delete from account_sessions account_session
                using due
                where account_session.id = due.id
                returning due.expires_at
            )
            select
                count(*)::integer as "Deleted",
                extract(epoch from now() - min(deleted.expires_at))::double precision as "OldestDueAgeSeconds"
            from deleted;
            """,
            new { WindowSeconds = batch.Window.TotalSeconds, BatchSize = batch.Size },
            scope.Transaction,
            cancellationToken: cancellationToken));
        return row.ToResult();
    }
}
