namespace Mmorpg.GameService.Items.Persistence;

internal static class MobCorpseOwnershipMigration
{
    public const string Id = "202611041800_mob_corpse_ownership";

    // A Mob's corpse belongs to the player the Mob credited, who alone may loot it, and the
    // corpses that a world boss leaves for each credited player share one loot group. A corpse
    // without an owner, as every player corpse, is open to everyone.
    public const string Sql = """
        alter table corpses
            add column owner_character_id uuid null references characters(id) on delete set null,
            add column loot_group_id uuid null,
            add constraint ck_corpses_owner check (
                owner_character_id is null or source_type = 'persistent_npc'),
            add constraint ck_corpses_loot_group check (
                loot_group_id is null or source_type = 'persistent_npc');
        """;
}
