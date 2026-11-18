namespace Mmorpg.GameService.Placement.Persistence;

internal static class SimulationSessionGenerationMigration
{
    public const string Id = "202611181200_simulation_session_generation";

    // How many times the session's token was issued: 1 when the session is created, one more with
    // every reconnect's consumption of a join ticket, which rotates the token. The consume answer
    // carries it, so the worker can tell which of two joins of one session consumed last, whatever
    // order their answers reach it in. Expand only: a default, so the version before still inserts
    // and updates sessions without it.
    public const string Sql = """
        alter table character_simulation_sessions
            add column session_generation bigint not null default 1,
            add constraint ck_character_simulation_sessions_generation
                check (session_generation >= 1);
        """;
}
