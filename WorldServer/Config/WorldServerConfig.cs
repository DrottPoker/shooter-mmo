using System.Globalization;
using Microsoft.Extensions.Configuration;
using ShooterMmo.GameSimulation;
using ShooterMmo.Shared.Networking;

namespace WorldServer.Config;

public sealed record WorldServerConfig(
    string WorldServerId,
    string CollisionDataPath,
    int UdpPort,
    string AdvertisedHost,
    int AdvertisedUdpPort,
    int MaxConnections,
    TimeSpan JoinHandshakeTimeout,
    TimeSpan NetworkPollInterval,
    int SnapshotRateHz,
    TimeSpan MovementInputSilenceTimeout,
    MovementSimulationSettings MovementSimulation,
    MovementSpawnConfig MovementSpawn,
    Uri AuthServiceBaseUrl,
    TimeSpan AuthServiceTimeout,
    string AuthServiceSecret,
    TimeSpan WorldSessionHeartbeatInterval,
    TimeSpan WorldRegistryHeartbeatInterval,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout)
{
    public int WorldSessionHeartbeatMaxConcurrency { get; init; } = 8;

    public TimeSpan NetworkMetricsLogInterval { get; init; } = TimeSpan.FromSeconds(30);

    public UdpQuotaConfig UdpQuotas { get; init; } = UdpQuotaConfig.Default;

    public InterestManagementConfig InterestManagement { get; init; } =
        InterestManagementConfig.Default;

    public CollisionStreamingConfig CollisionStreaming { get; init; } =
        CollisionStreamingConfig.Default;

    public static WorldServerConfig FromConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var section = configuration.GetSection("WorldServer");

        var worldServerId = Require(
            First(section["WorldServerId"], configuration["WORLD_SERVER_ID"]),
            "WorldServer:WorldServerId",
            errors);
        var authServiceUrl = Require(
            First(section["AuthServiceBaseUrl"], configuration["AUTH_SERVICE_BASE_URL"]),
            "WorldServer:AuthServiceBaseUrl",
            errors);
        var authServiceSecret = Require(
            First(section["AuthServiceSecret"], configuration["WORLD_SERVER_SERVICE_SECRET"]),
            "WorldServer:AuthServiceSecret",
            errors);
        var collisionDataPath = Require(
            First(
                section["CollisionDataPath"],
                configuration["WORLD_COLLISION_DATA_PATH"]),
            "WorldServer:CollisionDataPath",
            errors);
        var redis = Require(configuration.GetConnectionString("Redis"), "ConnectionStrings:Redis", errors);

        var udpPort = PositiveInt(
            First(section["UdpPort"], configuration["WORLD_UDP_PORT"]),
            "WorldServer:UdpPort",
            errors);
        var advertisedHost = Require(
            First(section["AdvertisedHost"], configuration["WORLD_ADVERTISED_HOST"]),
            "WorldServer:AdvertisedHost",
            errors);
        var advertisedUdpPort = PositiveInt(
            First(section["AdvertisedUdpPort"], configuration["WORLD_ADVERTISED_UDP_PORT"]),
            "WorldServer:AdvertisedUdpPort",
            errors);
        var maxConnections = PositiveInt(
            First(section["MaxConnections"], configuration["WORLD_MAX_CONNECTIONS"]),
            "WorldServer:MaxConnections",
            errors);
        var joinHandshakeTimeoutSeconds = PositiveInt(
            First(
                section["JoinHandshakeTimeoutSeconds"],
                configuration["WORLD_JOIN_HANDSHAKE_TIMEOUT_SECONDS"]),
            "WorldServer:JoinHandshakeTimeoutSeconds",
            errors);
        var networkPollIntervalMilliseconds = PositiveInt(
            First(
                section["NetworkPollIntervalMilliseconds"],
                configuration["WORLD_NETWORK_POLL_INTERVAL_MILLISECONDS"]),
            "WorldServer:NetworkPollIntervalMilliseconds",
            errors);
        var movementSection = section.GetSection("Movement");
        var movementTickRateHz = PositiveInt(
            movementSection["TickRateHz"],
            "WorldServer:Movement:TickRateHz",
            errors);
        var snapshotRateHz = PositiveInt(
            movementSection["SnapshotRateHz"],
            "WorldServer:Movement:SnapshotRateHz",
            errors);
        var movementInputSilenceTimeoutMilliseconds = PositiveInt(
            movementSection["InputSilenceTimeoutMilliseconds"],
            "WorldServer:Movement:InputSilenceTimeoutMilliseconds",
            errors);
        var walkSpeed = FiniteFloat(
            movementSection["WalkSpeed"],
            "WorldServer:Movement:WalkSpeed",
            errors);
        var sprintSpeed = FiniteFloat(
            movementSection["SprintSpeed"],
            "WorldServer:Movement:SprintSpeed",
            errors);
        var rotationSpeed = FiniteFloat(
            movementSection["RotationSpeedDegrees"],
            "WorldServer:Movement:RotationSpeedDegrees",
            errors);
        var gravity = FiniteFloat(
            movementSection["Gravity"],
            "WorldServer:Movement:Gravity",
            errors);
        var maximumFallSpeed = FiniteFloat(
            movementSection["MaximumFallSpeed"],
            "WorldServer:Movement:MaximumFallSpeed",
            errors);
        var jumpVelocity = FiniteFloat(
            movementSection["JumpVelocity"],
            "WorldServer:Movement:JumpVelocity",
            errors);
        var groundedVerticalVelocity = FiniteFloat(
            movementSection["GroundedVerticalVelocity"],
            "WorldServer:Movement:GroundedVerticalVelocity",
            errors);
        var groundHeight = FiniteFloat(
            movementSection["GroundHeight"],
            "WorldServer:Movement:GroundHeight",
            errors);
        var minimumX = FiniteFloat(
            movementSection["MinimumX"],
            "WorldServer:Movement:MinimumX",
            errors);
        var maximumX = FiniteFloat(
            movementSection["MaximumX"],
            "WorldServer:Movement:MaximumX",
            errors);
        var minimumZ = FiniteFloat(
            movementSection["MinimumZ"],
            "WorldServer:Movement:MinimumZ",
            errors);
        var maximumZ = FiniteFloat(
            movementSection["MaximumZ"],
            "WorldServer:Movement:MaximumZ",
            errors);
        var characterRadius = FiniteFloat(
            movementSection["CharacterRadius"],
            "WorldServer:Movement:CharacterRadius",
            errors);
        var characterHeight = FiniteFloat(
            movementSection["CharacterHeight"],
            "WorldServer:Movement:CharacterHeight",
            errors);
        var stepHeight = FiniteFloat(
            movementSection["StepHeight"],
            "WorldServer:Movement:StepHeight",
            errors);
        var maximumSlopeDegrees = FiniteFloat(
            movementSection["MaximumSlopeDegrees"],
            "WorldServer:Movement:MaximumSlopeDegrees",
            errors);
        var groundSnapDistance = FiniteFloat(
            movementSection["GroundSnapDistance"],
            "WorldServer:Movement:GroundSnapDistance",
            errors);
        var maximumSubstepDistance = FiniteFloat(
            movementSection["MaximumSubstepDistance"],
            "WorldServer:Movement:MaximumSubstepDistance",
            errors);
        var maximumPenetrationIterations = PositiveInt(
            movementSection["MaximumPenetrationIterations"],
            "WorldServer:Movement:MaximumPenetrationIterations",
            errors);
        var spawnX = FiniteFloat(
            movementSection["SpawnX"],
            "WorldServer:Movement:SpawnX",
            errors);
        var spawnY = FiniteFloat(
            movementSection["SpawnY"],
            "WorldServer:Movement:SpawnY",
            errors);
        var spawnZ = FiniteFloat(
            movementSection["SpawnZ"],
            "WorldServer:Movement:SpawnZ",
            errors);
        var spawnYaw = FiniteFloat(
            movementSection["SpawnYawDegrees"],
            "WorldServer:Movement:SpawnYawDegrees",
            errors);
        var authTimeoutSeconds = PositiveInt(
            First(section["AuthServiceTimeoutSeconds"], configuration["AUTH_SERVICE_TIMEOUT_SECONDS"]),
            "WorldServer:AuthServiceTimeoutSeconds",
            errors);
        var sessionHeartbeatSeconds = PositiveInt(
            First(section["WorldSessionHeartbeatSeconds"], configuration["WORLD_SESSION_HEARTBEAT_SECONDS"]),
            "WorldServer:WorldSessionHeartbeatSeconds",
            errors);
        var sessionHeartbeatMaxConcurrency = PositiveInt(
            section["WorldSessionHeartbeatMaxConcurrency"],
            "WorldServer:WorldSessionHeartbeatMaxConcurrency",
            errors);
        var registryHeartbeatSeconds = PositiveInt(
            First(section["WorldRegistryHeartbeatSeconds"], configuration["WORLD_REGISTRY_HEARTBEAT_SECONDS"]),
            "WorldServer:WorldRegistryHeartbeatSeconds",
            errors);
        var networkMetricsLogSeconds = PositiveInt(
            section["NetworkMetricsLogSeconds"],
            "WorldServer:NetworkMetricsLogSeconds",
            errors);
        var udpQuotaSection = section.GetSection("UdpQuotas");
        var inboundPacketsPerSecond = PositiveInt(
            udpQuotaSection["InboundPacketsPerSecond"],
            "WorldServer:UdpQuotas:InboundPacketsPerSecond",
            errors);
        var inboundPacketBurst = PositiveInt(
            udpQuotaSection["InboundPacketBurst"],
            "WorldServer:UdpQuotas:InboundPacketBurst",
            errors);
        var inboundBytesPerSecond = PositiveInt(
            udpQuotaSection["InboundBytesPerSecond"],
            "WorldServer:UdpQuotas:InboundBytesPerSecond",
            errors);
        var inboundByteBurst = PositiveInt(
            udpQuotaSection["InboundByteBurst"],
            "WorldServer:UdpQuotas:InboundByteBurst",
            errors);
        var snapshotBytesPerSecond = PositiveInt(
            udpQuotaSection["SnapshotBytesPerSecond"],
            "WorldServer:UdpQuotas:SnapshotBytesPerSecond",
            errors);
        var snapshotByteBurst = PositiveInt(
            udpQuotaSection["SnapshotByteBurst"],
            "WorldServer:UdpQuotas:SnapshotByteBurst",
            errors);
        var interestSection = section.GetSection("InterestManagement");
        var interestCellSize = FiniteFloat(
            interestSection["CellSize"],
            "WorldServer:InterestManagement:CellSize",
            errors);
        var interestEnterRadius = FiniteFloat(
            interestSection["EnterRadius"],
            "WorldServer:InterestManagement:EnterRadius",
            errors);
        var interestExitRadius = FiniteFloat(
            interestSection["ExitRadius"],
            "WorldServer:InterestManagement:ExitRadius",
            errors);
        var collisionStreamingSection = section.GetSection("CollisionStreaming");
        var collisionLoadRadiusChunks = PositiveInt(
            collisionStreamingSection["LoadRadiusChunks"],
            "WorldServer:CollisionStreaming:LoadRadiusChunks",
            errors);
        var collisionUnloadRadiusChunks = PositiveInt(
            collisionStreamingSection["UnloadRadiusChunks"],
            "WorldServer:CollisionStreaming:UnloadRadiusChunks",
            errors);
        var healthTimeoutMilliseconds = PositiveInt(
            configuration["HealthChecks:TimeoutMilliseconds"],
            "HealthChecks:TimeoutMilliseconds",
            errors);

        if (udpPort > 65535)
        {
            errors.Add("WorldServer:UdpPort must be between 1 and 65535.");
        }

        if (advertisedUdpPort > 65535)
        {
            errors.Add("WorldServer:AdvertisedUdpPort must be between 1 and 65535.");
        }

        if (advertisedHost is not null
            && (advertisedHost.Length > 253
                || advertisedHost.Any(char.IsWhiteSpace)
                || advertisedHost.Contains("/", StringComparison.Ordinal)
                || advertisedHost.Contains("\\", StringComparison.Ordinal)))
        {
            errors.Add("WorldServer:AdvertisedHost must be a host name or IP address without a scheme or path.");
        }

        if (maxConnections > 10_000)
        {
            errors.Add("WorldServer:MaxConnections must not exceed 10000.");
        }

        if (networkPollIntervalMilliseconds > 1000)
        {
            errors.Add("WorldServer:NetworkPollIntervalMilliseconds must not exceed 1000.");
        }

        if (sessionHeartbeatMaxConcurrency > 128)
        {
            errors.Add("WorldServer:WorldSessionHeartbeatMaxConcurrency must not exceed 128.");
        }

        if (networkMetricsLogSeconds > 3_600)
        {
            errors.Add("WorldServer:NetworkMetricsLogSeconds must not exceed 3600.");
        }

        if (inboundPacketBurst < inboundPacketsPerSecond
            || inboundByteBurst < inboundBytesPerSecond
            || snapshotByteBurst < snapshotBytesPerSecond)
        {
            errors.Add("WorldServer UDP quota burst values must be greater than or equal to their sustained rates.");
        }

        if (inboundPacketsPerSecond > 10_000
            || inboundBytesPerSecond > 16 * 1024 * 1024
            || snapshotBytesPerSecond > 16 * 1024 * 1024
            || inboundPacketBurst > 20_000
            || inboundByteBurst > 32 * 1024 * 1024
            || snapshotByteBurst > 32 * 1024 * 1024)
        {
            errors.Add("WorldServer UDP quota rates or bursts exceed the supported safety limits.");
        }

        if (interestCellSize <= 0f
            || interestEnterRadius <= 0f
            || interestExitRadius < interestEnterRadius
            || interestCellSize > 4_096f
            || interestExitRadius > 10_000f
            || interestExitRadius / interestCellSize > 64f)
        {
            errors.Add("WorldServer interest management requires bounded positive radii, a positive cell size, an exit radius greater than or equal to the enter radius, and no more than 64 searched cells per axis direction.");
        }

        if (collisionUnloadRadiusChunks < collisionLoadRadiusChunks)
        {
            errors.Add("WorldServer:CollisionStreaming:UnloadRadiusChunks must be greater than or equal to LoadRadiusChunks.");
        }

        if (collisionUnloadRadiusChunks > 16)
        {
            errors.Add("WorldServer:CollisionStreaming:UnloadRadiusChunks must not exceed 16.");
        }

        if (snapshotRateHz > movementTickRateHz
            || movementTickRateHz % snapshotRateHz != 0)
        {
            errors.Add(
                "WorldServer:Movement:SnapshotRateHz must divide TickRateHz without a remainder.");
        }

        if (movementInputSilenceTimeoutMilliseconds > 5_000)
        {
            errors.Add(
                "WorldServer:Movement:InputSilenceTimeoutMilliseconds must not exceed 5000.");
        }

        MovementSimulationSettings? movementSimulation = null;
        try
        {
            var characterCollision = new CharacterCollisionSettings(
                characterRadius,
                characterHeight,
                stepHeight,
                maximumSlopeDegrees,
                groundSnapDistance,
                maximumSubstepDistance,
                maximumPenetrationIterations);
            movementSimulation = new MovementSimulationSettings(
                movementTickRateHz,
                walkSpeed,
                sprintSpeed,
                rotationSpeed,
                gravity,
                maximumFallSpeed,
                jumpVelocity,
                groundedVerticalVelocity,
                groundHeight,
                minimumX,
                maximumX,
                minimumZ,
                maximumZ,
                characterCollision);
        }
        catch (ArgumentException exception)
        {
            errors.Add($"WorldServer:Movement is invalid: {exception.Message}");
        }

        if (movementSimulation is not null
            && (spawnX < movementSimulation.MinimumX
                || spawnX > movementSimulation.MaximumX
                || spawnY < movementSimulation.GroundHeight
                || spawnZ < movementSimulation.MinimumZ
                || spawnZ > movementSimulation.MaximumZ))
        {
            errors.Add("WorldServer:Movement spawn must be inside the configured world bounds.");
        }

        var parsedAuthServiceUrl = ParseHttpUri(authServiceUrl, "WorldServer:AuthServiceBaseUrl", errors);

        if (authServiceSecret is not null && authServiceSecret.Length < 32)
        {
            errors.Add("WorldServer:AuthServiceSecret must be at least 32 characters.");
        }

        if (redis is not null)
        {
            try
            {
                ShooterMmo.Shared.Networking.RedisConnectionString.ParseRequiredEndpoint(redis);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add($"ConnectionStrings:Redis is invalid: {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "WorldServer configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new WorldServerConfig(
            worldServerId!,
            collisionDataPath!,
            udpPort,
            advertisedHost!,
            advertisedUdpPort,
            maxConnections,
            TimeSpan.FromSeconds(joinHandshakeTimeoutSeconds),
            TimeSpan.FromMilliseconds(networkPollIntervalMilliseconds),
            snapshotRateHz,
            TimeSpan.FromMilliseconds(movementInputSilenceTimeoutMilliseconds),
            movementSimulation!,
            new MovementSpawnConfig(spawnX, spawnY, spawnZ, spawnYaw),
            parsedAuthServiceUrl!,
            TimeSpan.FromSeconds(authTimeoutSeconds),
            authServiceSecret!,
            TimeSpan.FromSeconds(sessionHeartbeatSeconds),
            TimeSpan.FromSeconds(registryHeartbeatSeconds),
            redis!,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds))
        {
            WorldSessionHeartbeatMaxConcurrency = sessionHeartbeatMaxConcurrency,
            NetworkMetricsLogInterval = TimeSpan.FromSeconds(networkMetricsLogSeconds),
            UdpQuotas = new UdpQuotaConfig(
                inboundPacketsPerSecond,
                inboundPacketBurst,
                inboundBytesPerSecond,
                inboundByteBurst,
                snapshotBytesPerSecond,
                snapshotByteBurst),
            InterestManagement = new InterestManagementConfig(
                interestCellSize,
                interestEnterRadius,
                interestExitRadius),
            CollisionStreaming = new CollisionStreamingConfig(
                collisionLoadRadiusChunks,
                collisionUnloadRadiusChunks)
        };
    }

    private static string? Require(string? value, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required.");
            return null;
        }

        return value.Trim();
    }

    private static string? First(string? primary, string? alias)
    {
        return string.IsNullOrWhiteSpace(alias) ? primary : alias;
    }

    private static int PositiveInt(string? value, string key, ICollection<string> errors)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            errors.Add($"{key} must be a positive integer.");
            return 1;
        }

        return parsed;
    }

    private static float FiniteFloat(string? value, string key, ICollection<string> errors)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || float.IsNaN(parsed)
            || float.IsInfinity(parsed))
        {
            errors.Add($"{key} must be a finite number.");
            return 0f;
        }

        return parsed;
    }

    private static Uri? ParseHttpUri(string? value, string key, ICollection<string> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add($"{key} must be an absolute HTTP or HTTPS URL.");
            return null;
        }

        return uri;
    }
}

public sealed record MovementSpawnConfig(float X, float Y, float Z, float YawDegrees);

public sealed record UdpQuotaConfig(
    int InboundPacketsPerSecond,
    int InboundPacketBurst,
    int InboundBytesPerSecond,
    int InboundByteBurst,
    int SnapshotBytesPerSecond,
    int SnapshotByteBurst)
{
    public static UdpQuotaConfig Default { get; } = new(
        120,
        240,
        128 * 1024,
        256 * 1024,
        256 * 1024,
        512 * 1024);
}

public sealed record InterestManagementConfig(
    float CellSize,
    float EnterRadius,
    float ExitRadius)
{
    public static InterestManagementConfig Default { get; } = new(64f, 128f, 144f);
}

public sealed record CollisionStreamingConfig(
    int LoadRadiusChunks,
    int UnloadRadiusChunks)
{
    public static CollisionStreamingConfig Default { get; } = new(2, 3);
}
