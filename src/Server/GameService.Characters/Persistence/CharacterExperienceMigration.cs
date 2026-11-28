namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterExperienceMigration
{
    public const string Id = "202611281200_character_experience";

    // The experience a character has gathered inside its current level, never a total, so a
    // change of the level curve never changes a level that a character already has.
    public const string Sql = """
        alter table characters
            add column experience bigint not null default 0,
            add constraint ck_characters_experience check (experience >= 0);
        """;
}
