namespace AuthService.Database.Migrations;

public static class ItemOperationsHardeningMigration
{
    public const string Id = "202607181800_item_operations_hardening";

    public const string Sql = """
        create index ix_item_operations_retention
            on item_operations(completed_at, operation_id)
            where completed_at is not null;

        create index ix_corpses_closed_retention
            on corpses(closed_at, id)
            where closed_at is not null;

        create index ix_recovery_deliveries_expiry
            on recovery_deliveries(expires_at, id)
            where claimed_at is null and expires_at is not null;
        """;
}
