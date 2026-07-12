using AuthService.Config;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Worlds;

public sealed class WorldRegistryService(NpgsqlDataSource dataSource, AuthServiceConfig config)
{
    public async Task<ServiceResult<WorldHeartbeatResponse>> HeartbeatAsync(
        string worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update worlds
            set is_online = true,
                last_heartbeat_at = now(),
                updated_at = now()
            where id = @WorldId
            returning id as "WorldId",
                      last_heartbeat_at as "LastHeartbeatAt",
                      last_heartbeat_at + make_interval(secs => @HeartbeatTimeoutSeconds) as "OnlineUntil";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var heartbeat = await connection.QuerySingleOrDefaultAsync<WorldHeartbeatResponse>(
            new CommandDefinition(
                sql,
                new
                {
                    WorldId = worldId,
                    HeartbeatTimeoutSeconds = (int)config.WorldHeartbeatTimeout.TotalSeconds
                },
                cancellationToken: cancellationToken));

        return heartbeat is null
            ? ServiceResult<WorldHeartbeatResponse>.NotFound("world_not_found", "World was not found.")
            : ServiceResult<WorldHeartbeatResponse>.Ok(heartbeat);
    }
}
