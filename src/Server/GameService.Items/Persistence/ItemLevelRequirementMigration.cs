namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemLevelRequirementMigration
{
    public const string Id = "202610181210_item_level_requirement";

    // The catalog's level requirement, mirrored so an equip can check it against the
    // character level in the same transaction. Every definition requires level 1 or more.
    public const string Sql = """
        alter table item_definitions
            add column required_character_level integer not null default 1,
            add constraint ck_item_definitions_required_character_level
                check (required_character_level >= 1);
        """;
}
