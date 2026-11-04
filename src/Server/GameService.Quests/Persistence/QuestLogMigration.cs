namespace Mmorpg.GameService.Quests.Persistence;

internal static class QuestLogMigration
{
    public const string Id = "202611042200_quest_log";

    // The mirrored quest catalog revision and quest identities, and every character's quest
    // log: the active quests with the progress of their current step, and the completed
    // quests with how often they were completed. Collect objectives count carried items, so
    // they store no progress. Quest ids are never removed, so stored state always names a
    // mirrored quest.
    public const string Sql = """
        create table quest_catalogs (
            catalog_id text primary key,
            revision text not null,
            mirrored_at timestamptz not null default now(),
            constraint ck_quest_catalogs_revision check (revision ~ '^[0-9a-f]{64}$')
        );

        create table quest_definitions (
            id text primary key,
            structural_fingerprint text not null,
            retired_at timestamptz null,
            mirrored_at timestamptz not null default now(),
            constraint ck_quest_definitions_fingerprint check (structural_fingerprint ~ '^[0-9a-f]{64}$')
        );

        create table character_quest_logs (
            character_id uuid primary key references characters(id) on delete cascade,
            revision bigint not null,
            constraint ck_character_quest_logs_revision check (revision >= 1)
        );

        create table character_active_quests (
            character_id uuid not null references character_quest_logs(character_id) on delete cascade,
            quest_id text not null references quest_definitions(id),
            step_id text not null,
            accepted_at timestamptz not null default now(),
            primary key (character_id, quest_id)
        );

        create index ix_character_active_quests_quest
            on character_active_quests (quest_id);

        create table character_quest_progress (
            character_id uuid not null,
            quest_id text not null,
            objective_id text not null,
            progress integer not null,
            primary key (character_id, quest_id, objective_id),
            constraint fk_character_quest_progress_active_quest foreign key (character_id, quest_id)
                references character_active_quests (character_id, quest_id) on delete cascade,
            constraint ck_character_quest_progress_range check (progress between 0 and 1000)
        );

        create table character_completed_quests (
            character_id uuid not null references character_quest_logs(character_id) on delete cascade,
            quest_id text not null references quest_definitions(id),
            completion_count integer not null,
            last_completed_at timestamptz not null,
            primary key (character_id, quest_id),
            constraint ck_character_completed_quests_count check (completion_count >= 1)
        );
        """;
}
