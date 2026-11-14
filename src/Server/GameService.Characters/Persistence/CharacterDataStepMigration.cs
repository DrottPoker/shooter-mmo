namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterDataStepMigration
{
    public const string Id = "202611141400_character_data_steps";

    // Data steps that run over every character and record the content revision they last ran
    // for to completion, so a start with unchanged content skips them. A crash or a refusal
    // before the record leaves the old revision, and the next run starts the step again.
    public const string Sql = """
        create table character_data_steps (
            id text primary key,
            revision text not null,
            completed_at timestamptz not null default now(),
            constraint ck_character_data_steps_id check (id ~ '^[a-z][a-z0-9_]{0,63}$'),
            constraint ck_character_data_steps_revision check (length(revision) between 1 and 128)
        );
        """;
}
