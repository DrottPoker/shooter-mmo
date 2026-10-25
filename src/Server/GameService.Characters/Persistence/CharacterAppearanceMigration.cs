namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterAppearanceMigration
{
    public const string Id = "202610251200_character_appearance";

    // Every character has one look: its body model, the choices of its look as an array of
    // { kind, slot, option } objects, which GameService checks against the appearance content,
    // and whether it has lowered its headwear. Existing characters get the default look of the
    // male body, as every client showed them before.
    public const string Sql = """
        create table character_appearances (
            character_id uuid primary key references characters(id) on delete cascade,
            body_model text not null,
            choices jsonb not null,
            headwear_lowered boolean not null default false,
            updated_at timestamptz not null default now(),
            constraint ck_character_appearances_body_model check (body_model ~ '^[A-Za-z][A-Za-z0-9_]{0,63}$'),
            constraint ck_character_appearances_choices check (jsonb_typeof(choices) = 'array')
        );

        insert into character_appearances (character_id, body_model, choices)
        select id,
               'Human_Male',
               '[{"kind":"face","slot":"","option":"Stubble"},{"kind":"eyes","slot":"","option":"Teal"},{"kind":"underwear","slot":"legs","option":"Briefs"}]'::jsonb
        from characters;
        """;
}
