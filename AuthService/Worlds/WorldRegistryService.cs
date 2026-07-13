using AuthService.Config;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Worlds;

public sealed class WorldRegistryService(NpgsqlDataSource dataSource, AuthServiceConfig config)
{
    public async Task<ServiceResult<WorldHeartbeatResponse>> HeartbeatAsync(
        string worldId,
        WorldHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return ServiceResult<WorldHeartbeatResponse>.BadRequest(
                validationError.Value.Code,
                validationError.Value.Message);
        }

        const string sql = """
            update worlds
            set is_online = true,
                host = @Host,
                udp_port = @UdpPort,
                instance_id = @InstanceId,
                protocol_version = @ProtocolVersion,
                simulation_revision = @SimulationRevision,
                collision_revision = @CollisionRevision,
                last_heartbeat_at = now(),
                updated_at = now()
            where id = @WorldId
            returning id as "WorldId",
                      host as "Host",
                      udp_port as "UdpPort",
                      instance_id as "InstanceId",
                      protocol_version as "ProtocolVersion",
                      simulation_revision as "SimulationRevision",
                      collision_revision as "CollisionRevision",
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
                    Host = request.Host!.Trim(),
                    request.UdpPort,
                    InstanceId = request.InstanceId!.Trim(),
                    request.ProtocolVersion,
                    SimulationRevision = request.SimulationRevision!.Trim(),
                    CollisionRevision = request.CollisionRevision!.Trim(),
                    HeartbeatTimeoutSeconds = (int)config.WorldHeartbeatTimeout.TotalSeconds
                },
                cancellationToken: cancellationToken));

        return heartbeat is null
            ? ServiceResult<WorldHeartbeatResponse>.NotFound("world_not_found", "World was not found.")
            : ServiceResult<WorldHeartbeatResponse>.Ok(heartbeat);
    }

    public async Task<ServiceResult<WorldOfflineResponse>> MarkOfflineAsync(
        string worldId,
        WorldOfflineRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstanceId)
            || request.InstanceId.Trim().Length > 128
            || request.InstanceId.Any(char.IsWhiteSpace))
        {
            return ServiceResult<WorldOfflineResponse>.BadRequest(
                "invalid_world_instance",
                "World instance id is invalid.");
        }

        const string sql = """
            update worlds
            set is_online = false,
                updated_at = now()
            where id = @WorldId
              and instance_id = @InstanceId
            returning id as "WorldId",
                      instance_id as "InstanceId",
                      updated_at as "OfflineAt";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var offline = await connection.QuerySingleOrDefaultAsync<WorldOfflineResponse>(
            new CommandDefinition(
                sql,
                new { WorldId = worldId, InstanceId = request.InstanceId.Trim() },
                cancellationToken: cancellationToken));
        if (offline is not null)
        {
            return ServiceResult<WorldOfflineResponse>.Ok(offline);
        }

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from worlds where id = @WorldId);",
            new { WorldId = worldId },
            cancellationToken: cancellationToken));
        return exists
            ? ServiceResult<WorldOfflineResponse>.Conflict(
                "world_instance_changed",
                "A different WorldServer instance currently owns this world registration.")
            : ServiceResult<WorldOfflineResponse>.NotFound(
                "world_not_found",
                "World was not found.");
    }

    private static (string Code, string Message)? Validate(WorldHeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host)
            || request.Host.Length > 253
            || request.Host.Any(char.IsWhiteSpace)
            || request.Host.Contains('/', StringComparison.Ordinal)
            || request.Host.Contains('\\', StringComparison.Ordinal))
        {
            return ("invalid_world_host", "World host is invalid.");
        }

        if (request.UdpPort is <= 0 or > 65535)
        {
            return ("invalid_world_port", "World UDP port is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.InstanceId)
            || request.InstanceId.Trim().Length > 128
            || request.InstanceId.Any(char.IsWhiteSpace))
        {
            return ("invalid_world_instance", "World instance id is invalid.");
        }

        if (request.ProtocolVersion is <= 0 or > ushort.MaxValue)
        {
            return ("invalid_protocol_version", "Realtime protocol version is invalid.");
        }

        if (!IsValidRevision(request.SimulationRevision)
            || !IsValidRevision(request.CollisionRevision))
        {
            return ("invalid_world_revision", "World build revision metadata is invalid.");
        }

        return null;
    }

    private static bool IsValidRevision(string? revision)
    {
        return !string.IsNullOrWhiteSpace(revision)
            && revision.Trim().Length <= 128
            && !revision.Any(char.IsWhiteSpace);
    }
}
