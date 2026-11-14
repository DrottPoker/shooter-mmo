namespace Mmorpg.GameService.Placement.Persistence;

internal static class PlacementForeignKeyIndexMigration
{
    public const string Id = "202611141210_placement_foreign_key_indexes";

    // Every foreign key needs an index that leads with its columns, as ForeignKeyIndexTests
    // checks. Sessions and tickets grow with play, and their indexes covered only the
    // unreleased or unconsumed rows, which cannot serve a key. A widened index keeps the
    // released or consumed time right after the column it shares, so the active rows of a
    // worker, Shard, or account still lie together. The unique active indexes stay, because
    // they enforce one active row. The tickets of an account and of a Shard had no index at all.
    // The migration builds the indexes inside its transaction, which blocks writes to each table
    // while it builds. That is fine at today's sizes; a large live table needs create index
    // concurrently, which runs outside a transaction and the migration runner cannot run yet.
    public const string Sql = """
        create index ix_simulation_assignments_worker_id
            on simulation_assignments(worker_id);

        create index ix_character_simulation_sessions_account
            on character_simulation_sessions(account_id);

        create index ix_character_simulation_sessions_character
            on character_simulation_sessions(character_id);

        drop index ix_character_simulation_sessions_account_session_id;

        create index ix_character_simulation_sessions_account_session_id
            on character_simulation_sessions(account_session_id);

        drop index ix_character_simulation_sessions_active_shard;

        create index ix_character_simulation_sessions_shard
            on character_simulation_sessions(shard_id, released_at);

        drop index ix_character_simulation_sessions_worker_runtime;

        create index ix_character_simulation_sessions_worker_runtime
            on character_simulation_sessions(simulation_worker_id, released_at, worker_runtime_id);

        create index ix_simulation_join_tickets_account
            on simulation_join_tickets(account_id, consumed_at);

        create index ix_simulation_join_tickets_shard
            on simulation_join_tickets(shard_id, consumed_at);

        create index ix_simulation_join_tickets_simulation_session
            on simulation_join_tickets(simulation_session_id);

        drop index ix_simulation_join_tickets_account_session_id;

        create index ix_simulation_join_tickets_account_session_id
            on simulation_join_tickets(account_session_id);

        drop index ix_simulation_join_tickets_worker_runtime;

        create index ix_simulation_join_tickets_worker_runtime
            on simulation_join_tickets(simulation_worker_id, consumed_at, worker_runtime_id);
        """;
}
