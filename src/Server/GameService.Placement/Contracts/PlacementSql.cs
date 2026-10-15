namespace Mmorpg.GameService.Placement.Contracts;

// Read-only SQL fragments over Placement tables for other modules' statements. Each
// argument is a trusted SQL expression from the caller's own statement, never a user
// value. The fragments use their own aliases so they never collide with the caller's.
public static class PlacementSql
{
    public static string ShardExists(string shardIdExpression)
    {
        return $"""
            exists (
                select 1
                from shards placement_sql_shard
                where placement_sql_shard.id = {shardIdExpression})
            """;
    }

    // True while the character has an unreleased, unexpired simulation session lease.
    public static string HasActiveSimulationSession(string characterIdExpression)
    {
        return $"""
            exists (
                select 1
                from character_simulation_sessions placement_sql_session
                where placement_sql_session.character_id = {characterIdExpression}
                  and placement_sql_session.released_at is null
                  and placement_sql_session.expires_at > now())
            """;
    }
}
