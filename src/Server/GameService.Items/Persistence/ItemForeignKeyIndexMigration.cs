namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemForeignKeyIndexMigration
{
    public const string Id = "202611141220_item_foreign_keys_and_indexes";

    // Every foreign key needs an index that leads with its columns, as ForeignKeyIndexTests
    // checks, or every delete of a parent row scans the child table.
    // - The journal keeps the id of an item that is gone, and a destruction keeps the id of its
    //   operation, which retention may remove first: both become ids without a foreign key, as
    //   character_currency_ledger.item_operation_id is. Without its key, deleting an item no
    //   longer scans the journal.
    // - The policy lookup covered only active policies, so it could not serve the cascade from
    //   items; without the condition it serves both.
    // - No query reads item operations by request hash.
    // The migration builds the indexes inside its transaction, which blocks writes to each table
    // while it builds. That is fine at today's sizes; a large live table needs a migration from
    // DatabaseMigration.CreateIndexConcurrently, which the runner runs outside a transaction.
    public const string Sql = """
        alter table item_operation_changes
            drop constraint item_operation_changes_item_instance_id_fkey;

        alter table item_destructions
            drop constraint item_destructions_source_operation_id_fkey;

        drop index ix_item_operations_request_hash;

        drop index ix_item_instance_policies_lookup;

        create index ix_item_instance_policies_lookup
            on item_instance_policies(item_instance_id, policy_kind, revision);

        create index ix_item_containers_owner_character
            on item_containers(owner_character_id);

        create index ix_recovery_deliveries_recovery_storage_container
            on recovery_deliveries(recovery_storage_container_id);

        create index ix_corpses_owner_character
            on corpses(owner_character_id);

        create index ix_corpses_shard
            on corpses(shard_id);

        create index ix_death_events_character
            on death_events(character_id);
        """;
}
