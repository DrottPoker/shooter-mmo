namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterLevelMigration
{
    public const string Id = "202610181200_character_level";

    // The character level unlocks equipment through item level requirements. Every
    // existing and new character starts at level 1; experience is not part of the schema.
    public const string Sql = """
        alter table characters
            add column level integer not null default 1,
            add constraint ck_characters_level check (level >= 1);
        """;
}
