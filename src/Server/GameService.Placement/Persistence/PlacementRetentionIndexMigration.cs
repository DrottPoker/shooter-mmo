namespace Mmorpg.GameService.Placement.Persistence;

internal static class PlacementRetentionIndexMigration
{
    public const string Id = "202611141310_placement_retention_indexes";

    // The retention tasks delete the oldest ended rows first. A simulation session ended when it was
    // released or, unreleased, when its lease expired, and a join ticket when it was consumed or
    // expired, so their indexes hold that moment as one expression. The migration builds the
    // indexes inside its transaction, which blocks writes to each table while it builds. That is
    // fine at today's sizes; a large live table needs create index concurrently, which runs
    // outside a transaction and the migration runner cannot run yet.
    public const string Sql = """
        create index ix_character_simulation_sessions_retention
            on character_simulation_sessions((coalesce(released_at, expires_at)), id);

        create index ix_simulation_join_tickets_retention
            on simulation_join_tickets((coalesce(consumed_at, expires_at)), id);

        create index ix_simulation_assignments_retention
            on simulation_assignments(released_at, id)
            where released_at is not null;
        """;
}
