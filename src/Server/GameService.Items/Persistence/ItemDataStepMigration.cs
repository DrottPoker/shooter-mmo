namespace Mmorpg.GameService.Items.Persistence;

internal static class ItemDataStepMigration
{
    public const string Id = "202611141410_item_data_steps";

    // Data steps that run over every character and record the content revision they last ran
    // for to completion, so a start with unchanged content skips them. A crash before the
    // record leaves the old revision, and the next run starts the step again.
    public const string Sql = """
        create table item_data_steps (
            id text primary key,
            revision text not null,
            completed_at timestamptz not null default now(),
            constraint ck_item_data_steps_id check (id ~ '^[a-z][a-z0-9_]{0,63}$'),
            constraint ck_item_data_steps_revision check (length(revision) between 1 and 128)
        );
        """;
}
