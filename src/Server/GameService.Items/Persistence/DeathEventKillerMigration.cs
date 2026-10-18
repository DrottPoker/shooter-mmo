namespace Mmorpg.GameService.Items.Persistence;

internal static class DeathEventKillerMigration
{
    public const string Id = "202610181220_death_event_killer";

    // A combat death records who dealt the lethal hit, a Mob definition or a character,
    // for audit and later reputation rules. Development deaths have no killer.
    public const string Sql = """
        alter table death_events
            add column killer_kind text null,
            add column killer_id text null,
            add constraint ck_death_events_killer check (
                (killer_kind is null and killer_id is null)
                or (killer_kind in ('mob', 'character')
                    and killer_id is not null
                    and length(killer_id) between 1 and 128));
        """;
}
