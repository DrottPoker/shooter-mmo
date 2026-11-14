namespace Mmorpg.GameService.Items.Persistence;

internal static class CharacterItemStateBackfillMigration
{
    public const string Id = "202611141420_character_item_state_backfill";

    // Character creation bootstraps a character's item state in its own transaction, so only a
    // character from before the item foundation can lack one. Every start used to search for
    // such characters; this migration does it once instead. On every database that started
    // GameService since the item foundation it finds none and writes nothing. The state needs
    // the settings of a mirrored item catalog, so a database that holds such characters but
    // never mirrored a catalog stops here and needs a reset.
    public const string Sql = """
        do $$
        begin
            if not exists (
                select 1
                from characters character
                where character.deleted_at is null
                  and not exists (
                      select 1
                      from character_item_states state
                      where state.character_id = character.id)) then
                return;
            end if;

            if not exists (select 1 from item_catalog_revisions where is_current) then
                raise exception 'Characters without item state exist, but no item catalog was ever mirrored to create it.'
                    using errcode = '23514';
            end if;

            perform bootstrap_character_item_state(character.id)
            from characters character
            where character.deleted_at is null
              and not exists (
                  select 1
                  from character_item_states state
                  where state.character_id = character.id)
            order by character.id;
        end
        $$;
        """;
}
