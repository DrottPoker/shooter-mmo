namespace AuthService.Database.Migrations;

public static class PlayerDeathCarryOverflowMigration
{
    public const string Id = "202607171210_player_death_carry_overflow";

    public const string Sql = """
        alter table character_item_states
            drop constraint ck_character_item_states_hard_cap;
        """;
}
