namespace AuthService.Database.Migrations;

public static class ReleaseRetryableOperationClaimsMigration
{
    public const string Id = "202610141800_release_retryable_operation_claims";

    // Earlier versions stored rejections of retried system operations and transient
    // database timeouts, which blocked every later attempt with the same id. A rejected
    // operation never commits item changes, so its claim row can be released.
    public const string Sql = """
        delete from item_operations
        where status = 'rejected'
          and (
              operation_kind in (
                  'process_player_death',
                  'create_persistent_mob_corpse',
                  'grant_mob_loot',
                  'expire_corpse',
                  'expire_recovery_delivery')
              or result_payload -> 'error' ->> 'code' = 'item_transaction_timeout');
        """;
}
