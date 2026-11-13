namespace Mmorpg.GameService.Placement.Persistence;

internal static class SimulationSessionAccountEndMigration
{
    public const string Id = "202611131200_simulation_session_account_end";

    // When the account session of a simulation session ended. A logout or a new login marks the
    // session instead of releasing it, so the character's body stays in the world its full time,
    // and a reconnect of the same character by a new account session clears the mark.
    public const string Sql = """
        alter table character_simulation_sessions
            add column account_session_ended_at timestamptz null,
            add constraint ck_character_simulation_sessions_account_session_end
                check (account_session_ended_at is null or account_session_ended_at >= created_at);
        """;
}
