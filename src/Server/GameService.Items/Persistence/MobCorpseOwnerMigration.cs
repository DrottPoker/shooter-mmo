namespace Mmorpg.GameService.Items.Persistence;

internal static class MobCorpseOwnerMigration
{
    public const string Id = "202611041800_mob_corpse_owner";

    // A Mob's corpse belongs to the player who tagged the Mob, who alone may loot it. A corpse
    // without an owner, as every player corpse, is open to everyone.
    public const string Sql = """
        alter table corpses
            add column owner_character_id uuid null references characters(id) on delete set null,
            add constraint ck_corpses_owner check (
                owner_character_id is null or source_type = 'persistent_npc');
        """;
}
