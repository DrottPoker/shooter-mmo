namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterForeignKeyIndexMigration
{
    public const string Id = "202611141200_character_foreign_key_indexes";

    // Every foreign key needs an index that leads with its columns, as ForeignKeyIndexTests
    // checks. The account index covered only characters that are not deleted, so it could not
    // serve the key; without the condition it serves both the key and the account's list.
    // The migration builds it inside its transaction, which blocks writes to the table while it
    // builds. That is fine at today's sizes; a large live table needs a migration from
    // DatabaseMigration.CreateIndexConcurrently, which the runner runs outside a transaction.
    public const string Sql = """
        drop index ix_characters_account_id;

        create index ix_characters_account_id
            on characters(account_id);
        """;
}
