namespace Mmorpg.GameService.Characters.Persistence;

internal static class CharacterSimulationStateMigration
{
    public const string Id = "202611121200_character_simulation_states";

    // One row per character with its last saved place and vitals: the World and that World's
    // collision revision when the state was saved, the position and facing as the simulation's
    // floats, health and mana, and the simulation session that wrote it with its sequence within
    // that session. A World id or collision revision names no row of another module, so neither
    // has a foreign key. The bounds follow the save validation; a NaN or infinite real is
    // outside every range.
    public const string Sql = """
        create table character_simulation_states (
            character_id uuid primary key references characters(id) on delete cascade,
            world_id text not null,
            collision_revision text not null,
            position_x real not null,
            position_y real not null,
            position_z real not null,
            yaw_degrees real not null,
            health integer not null,
            mana integer not null,
            simulation_session_id uuid not null,
            session_sequence bigint not null,
            saved_at timestamptz not null default now(),
            constraint ck_character_simulation_states_world_id
                check (world_id ~ '^[A-Za-z0-9_-]{1,128}$'),
            constraint ck_character_simulation_states_collision_revision
                check (length(collision_revision) between 1 and 128 and collision_revision !~ '\s'),
            constraint ck_character_simulation_states_position
                check (position_x between -1000000 and 1000000
                    and position_y between -1000000 and 1000000
                    and position_z between -1000000 and 1000000),
            constraint ck_character_simulation_states_yaw
                check (yaw_degrees between -360 and 360),
            constraint ck_character_simulation_states_health
                check (health between 1 and 1000000),
            constraint ck_character_simulation_states_mana
                check (mana between 0 and 1000000),
            constraint ck_character_simulation_states_session_sequence
                check (session_sequence > 0)
        );
        """;
}
