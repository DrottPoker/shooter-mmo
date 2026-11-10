namespace Mmorpg.GameService.Items.Persistence;

internal static class CorpseSourceCharacterNameMigration
{
    public const string Id = "202611101200_corpse_source_character_name";

    // A corpse stores what it came from, never a display text. A player's corpse keeps the
    // name the character had, which the player chose. A Mob's corpse already stores its actor
    // definition, and the client names it from its text tables, so its name is dropped. The
    // check names "is not null" itself, because a check passes when its condition is unknown.
    public const string Sql = """
        alter table corpses
            drop constraint ck_corpses_source_display_name;

        alter table corpses
            rename column source_display_name to source_character_name;

        alter table corpses
            alter column source_character_name drop not null;

        update corpses
        set source_character_name = null
        where source_type = 'persistent_npc';

        alter table corpses
            add constraint ck_corpses_source_character_name check (
                (
                    source_type = 'player'
                    and source_character_name is not null
                    and length(btrim(source_character_name)) between 1 and 128
                )
                or
                (
                    source_type = 'persistent_npc'
                    and source_character_name is null
                ));

        -- The client names a Mob's corpse by its actor definition, so the definition must be
        -- there. The earlier check passed a Mob corpse without one for the same reason.
        alter table corpses
            drop constraint ck_corpses_source,
            add constraint ck_corpses_source check (
                (
                    source_type = 'player'
                    and source_npc_definition_id is null
                )
                or
                (
                    source_type = 'persistent_npc'
                    and source_character_id is null
                    and source_npc_definition_id is not null
                    and length(btrim(source_npc_definition_id)) between 1 and 128
                ));
        """;
}
