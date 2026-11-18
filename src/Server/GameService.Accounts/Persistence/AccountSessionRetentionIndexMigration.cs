namespace Mmorpg.GameService.Accounts.Persistence;

internal static class AccountSessionRetentionIndexMigration
{
    public const string Id = "202611141320_account_session_retention_index";

    // The retention of account sessions deletes the sessions whose token expired longest ago first.
    // The migration builds the index inside its transaction, which blocks writes to the table while
    // it builds. That is fine at today's sizes; a large live table needs a migration from
    // DatabaseMigration.CreateIndexConcurrently, which the runner runs outside a transaction.
    public const string Sql = """
        create index ix_account_sessions_retention
            on account_sessions(expires_at, id);
        """;
}
