using System.Globalization;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Networking;
using Mmorpg.Server.Common.Worlds;
using Mmorpg.WorldData.Worlds;

namespace Mmorpg.SimulationWorker.Config;

public sealed record SimulationWorkerConfig(
    string SimulationWorkerId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string WorldDataPath,
    int UdpPort,
    string AdvertisedHost,
    int AdvertisedUdpPort,
    int MaxConnections,
    TimeSpan JoinHandshakeTimeout,
    TimeSpan NetworkPollInterval,
    int SnapshotRateHz,
    MovementSimulationSettings MovementSimulation,
    MovementSpawnConfig MovementSpawn,
    Uri GameServiceBaseUrl,
    TimeSpan GameServiceTimeout,
    string GameServiceSecret,
    TimeSpan SimulationSessionHeartbeatInterval,
    TimeSpan RegistryHeartbeatInterval,
    string RedisConnectionString,
    TimeSpan HealthCheckTimeout)
{
    // How many session heartbeat requests, and releases at shutdown, run at once.
    public int SimulationSessionHeartbeatMaxConcurrency { get; init; } = 8;

    // How many sessions one session heartbeat request names at most.
    public int SimulationSessionHeartbeatBatchSize { get; init; } = DefaultSimulationSessionHeartbeatBatchSize;

    public const int DefaultSimulationSessionHeartbeatBatchSize = 250;

    // GameService takes at most this many sessions in one heartbeat request.
    public const int MaximumSimulationSessionHeartbeatBatchSize = 500;

    // How long a session heartbeat request that failed for a passing reason waits before it is
    // sent again, as long as the heartbeat interval lasts.
    public TimeSpan SimulationSessionHeartbeatRetryDelay { get; init; } =
        TimeSpan.FromMilliseconds(DefaultSimulationSessionHeartbeatRetryMilliseconds);

    public const int DefaultSimulationSessionHeartbeatRetryMilliseconds = 2_000;

    // How long a new runtime keeps retrying while a previous runtime's registry lease
    // is still live, before it concludes that another live runtime owns the worker id.
    public TimeSpan RegistryAcquireTimeout { get; init; } = TimeSpan.FromMinutes(2);

    // How soon a registry heartbeat that timed out, could not reach GameService, or got a server
    // error goes again, until one succeeds.
    public TimeSpan RegistryHeartbeatRetryDelay { get; init; } =
        TimeSpan.FromMilliseconds(DefaultRegistryHeartbeatRetryMilliseconds);

    public const int DefaultRegistryHeartbeatRetryMilliseconds = 2_000;

    // How long the worker plays on without a successful registry heartbeat before it stops in
    // order. Session leases must outlast it, so GameService still holds every session when the
    // worker comes back within it.
    public TimeSpan GameServiceOutageTolerance { get; init; } =
        TimeSpan.FromSeconds(DefaultGameServiceOutageToleranceSeconds);

    public const int DefaultGameServiceOutageToleranceSeconds = 120;
    public const int MinimumGameServiceOutageToleranceSeconds = 10;
    public const int MaximumGameServiceOutageToleranceSeconds = 600;

    // How long a death, a quest kill credit, or a Mob corpse whose outcome stays uncertain is
    // retried, counted only while GameService answers its registry heartbeat, before it is given up.
    public TimeSpan UncertainOperationRetryHorizon { get; init; } =
        TimeSpan.FromSeconds(DefaultUncertainOperationRetryHorizonSeconds);

    public const int DefaultUncertainOperationRetryHorizonSeconds = 600;
    public const int MaximumUncertainOperationRetryHorizonSeconds = 3_600;

    // The horizon must leave this much more than the outage tolerance.
    public const int MinimumUncertainOperationRetryHorizonMarginSeconds = 60;

    // The shortest session lease that a join may grant: the worker must hold the session through
    // a whole outage that it tolerates and the session heartbeat round that renews it afterwards.
    public TimeSpan MinimumSessionLease => GameServiceOutageTolerance + SimulationSessionHeartbeatInterval;

    public TimeSpan NetworkMetricsLogInterval { get; init; } = TimeSpan.FromSeconds(30);

    // Optional file that the metrics reporter appends one JSON line per interval to, for the
    // load tooling. Null writes no file.
    public string? PerformanceReportPath { get; init; }

    // How often the realtime loop saves the place and vitals of the players whose state changed.
    public TimeSpan CharacterCheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);

    // How long a character's body stays in the world after its connection ended while its
    // session goes on, counted again from every hit it takes.
    public int LingeringBodySeconds { get; init; } = DefaultLingeringBodySeconds;

    public const int DefaultLingeringBodySeconds = 20;

    // The longest stay a body may have; it also fits the LeaveAccepted field.
    public const int MaximumLingeringBodySeconds = 3_600;

    // How many runs in a row one tick phase or loop step may fail before the worker stops.
    public int TickPhaseFailureLimit { get; init; } = DefaultTickPhaseFailureLimit;

    public const int DefaultTickPhaseFailureLimit = 30;

    public UdpQuotaConfig UdpQuotas { get; init; } = UdpQuotaConfig.Default;

    public AdmissionConfig Admission { get; init; } = AdmissionConfig.Default;

    public InterestManagementConfig InterestManagement { get; init; } =
        InterestManagementConfig.Default;

    public SnapshotReplicationConfig SnapshotReplication { get; init; } =
        SnapshotReplicationConfig.Default;

    public CollisionStreamingConfig CollisionStreaming { get; init; } =
        CollisionStreamingConfig.Default;

    public ItemInteractionConfig ItemInteraction { get; init; } = ItemInteractionConfig.Empty;

    public const string DefaultActorCatalogPath = "WorldData/Actors/core.actor-catalog.json";
    public const string DefaultItemCatalogPath = "WorldData/Items/core.item-catalog.json";
    public const string DefaultQuestCatalogPath = "WorldData/Quests/core.quest-catalog.json";
    public const string DefaultCombatRulesPath = "WorldData/Combat/combat-rules.json";
    public const string DefaultCharacterAppearancePath = "WorldData/Characters/appearance.json";

    // The compiled actor catalog that every World shares.
    public string ActorCatalogPath { get; init; } = string.Empty;

    // The compiled actor spawns of the configured World, next to its world.json.
    public string ActorSpawnsPath { get; init; } = string.Empty;

    // The compiled item catalog that combat data and Mob loot come from.
    public string ItemCatalogPath { get; init; } = string.Empty;

    // The compiled quest catalog that the worker counts quest progress with.
    public string QuestCatalogPath { get; init; } = string.Empty;

    // The player combat rules and the meaning of every Shard rule set.
    public string CombatRulesPath { get; init; } = string.Empty;

    // The character appearance content: the equipment slots others see and the looks of bots.
    public string CharacterAppearancePath { get; init; } = string.Empty;

    public static SimulationWorkerConfig FromConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var section = configuration.GetSection("SimulationWorker");

        var simulationWorkerId = Require(
            First(section["SimulationWorkerId"], configuration["SIMULATION_WORKER_ID"]),
            "SimulationWorker:SimulationWorkerId",
            errors);
        var fleetId = Require(
            First(section["FleetId"], configuration["SIMULATION_FLEET_ID"]),
            "SimulationWorker:FleetId",
            errors);
        var nodeId = Require(
            First(section["NodeId"], configuration["SIMULATION_NODE_ID"]),
            "SimulationWorker:NodeId",
            errors);
        var shardId = Require(
            First(section["ShardId"], configuration["SIMULATION_SHARD_ID"]),
            "SimulationWorker:ShardId",
            errors);
        var worldId = Require(
            First(section["WorldId"], configuration["SIMULATION_WORLD_ID"]),
            "SimulationWorker:WorldId",
            errors);
        var gameServiceUrl = Require(
            First(section["GameServiceBaseUrl"], configuration["GAME_SERVICE_BASE_URL"]),
            "SimulationWorker:GameServiceBaseUrl",
            errors);
        var gameServiceSecret = Require(
            First(section["GameServiceSecret"], configuration["SIMULATION_WORKER_SERVICE_SECRET"]),
            "SimulationWorker:GameServiceSecret",
            errors);
        var worldDataPath = Require(
            First(
                section["WorldDataPath"],
                configuration["WORLD_DATA_PATH"]),
            "SimulationWorker:WorldDataPath",
            errors);
        WorldManifestDocument? worldManifest = null;
        if (worldDataPath is not null && worldId is not null)
        {
            try
            {
                worldManifest = WorldManifestFileStore.Load(worldDataPath, worldId);
            }
            catch (Exception exception) when (
                exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or WorldManifestValidationException)
            {
                errors.Add(
                    "SimulationWorker:WorldDataPath could not load the configured World: "
                    + exception.Message);
            }
        }
        var redis = Require(configuration.GetConnectionString("Redis"), "ConnectionStrings:Redis", errors);

        var udpPort = PositiveInt(
            First(section["UdpPort"], configuration["SIMULATION_WORKER_UDP_PORT"]),
            "SimulationWorker:UdpPort",
            errors);
        var advertisedHost = Require(
            First(
                section["AdvertisedHost"],
                configuration["SIMULATION_WORKER_ADVERTISED_HOST"]),
            "SimulationWorker:AdvertisedHost",
            errors);
        var advertisedUdpPort = PositiveInt(
            First(
                section["AdvertisedUdpPort"],
                configuration["SIMULATION_WORKER_ADVERTISED_UDP_PORT"]),
            "SimulationWorker:AdvertisedUdpPort",
            errors);
        var maxConnections = PositiveInt(
            First(
                section["MaxConnections"],
                configuration["SIMULATION_WORKER_MAX_CONNECTIONS"]),
            "SimulationWorker:MaxConnections",
            errors);
        var joinHandshakeTimeoutSeconds = PositiveInt(
            First(
                section["JoinHandshakeTimeoutSeconds"],
                configuration["SIMULATION_JOIN_HANDSHAKE_TIMEOUT_SECONDS"]),
            "SimulationWorker:JoinHandshakeTimeoutSeconds",
            errors);
        var networkPollIntervalMilliseconds = PositiveInt(
            First(
                section["NetworkPollIntervalMilliseconds"],
                configuration["SIMULATION_WORKER_NETWORK_POLL_INTERVAL_MILLISECONDS"]),
            "SimulationWorker:NetworkPollIntervalMilliseconds",
            errors);
        var movementSection = section.GetSection("Movement");
        var movementTickRateHz = PositiveInt(
            movementSection["TickRateHz"],
            "SimulationWorker:Movement:TickRateHz",
            errors);
        var snapshotRateHz = PositiveInt(
            movementSection["SnapshotRateHz"],
            "SimulationWorker:Movement:SnapshotRateHz",
            errors);
        var walkSpeed = FiniteFloat(
            movementSection["WalkSpeed"],
            "SimulationWorker:Movement:WalkSpeed",
            errors);
        var rotationSpeed = FiniteFloat(
            movementSection["RotationSpeedDegrees"],
            "SimulationWorker:Movement:RotationSpeedDegrees",
            errors);
        var gravity = FiniteFloat(
            movementSection["Gravity"],
            "SimulationWorker:Movement:Gravity",
            errors);
        var maximumFallSpeed = FiniteFloat(
            movementSection["MaximumFallSpeed"],
            "SimulationWorker:Movement:MaximumFallSpeed",
            errors);
        var groundedVerticalVelocity = FiniteFloat(
            movementSection["GroundedVerticalVelocity"],
            "SimulationWorker:Movement:GroundedVerticalVelocity",
            errors);
        var groundHeight = worldManifest?.groundHeight ?? 0f;
        var minimumX = worldManifest?.bounds.minimumX ?? 0f;
        var maximumX = worldManifest?.bounds.maximumX ?? 0f;
        var minimumZ = worldManifest?.bounds.minimumZ ?? 0f;
        var maximumZ = worldManifest?.bounds.maximumZ ?? 0f;
        var characterRadius = FiniteFloat(
            movementSection["CharacterRadius"],
            "SimulationWorker:Movement:CharacterRadius",
            errors);
        var characterHeight = FiniteFloat(
            movementSection["CharacterHeight"],
            "SimulationWorker:Movement:CharacterHeight",
            errors);
        var stepHeight = FiniteFloat(
            movementSection["StepHeight"],
            "SimulationWorker:Movement:StepHeight",
            errors);
        var maximumSlopeDegrees = FiniteFloat(
            movementSection["MaximumSlopeDegrees"],
            "SimulationWorker:Movement:MaximumSlopeDegrees",
            errors);
        var groundSnapDistance = FiniteFloat(
            movementSection["GroundSnapDistance"],
            "SimulationWorker:Movement:GroundSnapDistance",
            errors);
        var maximumSubstepDistance = FiniteFloat(
            movementSection["MaximumSubstepDistance"],
            "SimulationWorker:Movement:MaximumSubstepDistance",
            errors);
        var maximumPenetrationIterations = PositiveInt(
            movementSection["MaximumPenetrationIterations"],
            "SimulationWorker:Movement:MaximumPenetrationIterations",
            errors);
        var spawnX = worldManifest?.spawn.x ?? 0f;
        var spawnY = worldManifest?.spawn.y ?? 0f;
        var spawnZ = worldManifest?.spawn.z ?? 0f;
        var spawnYaw = worldManifest?.spawn.yawDegrees ?? 0f;
        var authTimeoutSeconds = PositiveInt(
            First(section["GameServiceTimeoutSeconds"], configuration["GAME_SERVICE_TIMEOUT_SECONDS"]),
            "SimulationWorker:GameServiceTimeoutSeconds",
            errors);
        var sessionHeartbeatSeconds = PositiveInt(
            First(section["SimulationSessionHeartbeatSeconds"], configuration["SIMULATION_SESSION_HEARTBEAT_SECONDS"]),
            "SimulationWorker:SimulationSessionHeartbeatSeconds",
            errors);
        var sessionHeartbeatMaxConcurrency = PositiveInt(
            section["SimulationSessionHeartbeatMaxConcurrency"],
            "SimulationWorker:SimulationSessionHeartbeatMaxConcurrency",
            errors);
        var sessionHeartbeatBatchSize = string.IsNullOrWhiteSpace(
                section["SimulationSessionHeartbeatBatchSize"])
            ? DefaultSimulationSessionHeartbeatBatchSize
            : PositiveInt(
                section["SimulationSessionHeartbeatBatchSize"],
                "SimulationWorker:SimulationSessionHeartbeatBatchSize",
                errors);
        var sessionHeartbeatRetryMilliseconds = string.IsNullOrWhiteSpace(
                section["SimulationSessionHeartbeatRetryMilliseconds"])
            ? DefaultSimulationSessionHeartbeatRetryMilliseconds
            : PositiveInt(
                section["SimulationSessionHeartbeatRetryMilliseconds"],
                "SimulationWorker:SimulationSessionHeartbeatRetryMilliseconds",
                errors);
        var registryHeartbeatSeconds = PositiveInt(
            First(
                section["RegistryHeartbeatSeconds"],
                configuration["SIMULATION_WORKER_REGISTRY_HEARTBEAT_SECONDS"]),
            "SimulationWorker:RegistryHeartbeatSeconds",
            errors);
        var registryAcquireTimeoutSeconds = string.IsNullOrWhiteSpace(
                section["RegistryAcquireTimeoutSeconds"])
            ? 120
            : PositiveInt(
                section["RegistryAcquireTimeoutSeconds"],
                "SimulationWorker:RegistryAcquireTimeoutSeconds",
                errors);
        var registryHeartbeatRetryMilliseconds = string.IsNullOrWhiteSpace(
                section["RegistryHeartbeatRetryMilliseconds"])
            ? DefaultRegistryHeartbeatRetryMilliseconds
            : PositiveInt(
                section["RegistryHeartbeatRetryMilliseconds"],
                "SimulationWorker:RegistryHeartbeatRetryMilliseconds",
                errors);
        var gameServiceOutageToleranceSeconds = string.IsNullOrWhiteSpace(
                section["GameServiceOutageToleranceSeconds"])
            ? DefaultGameServiceOutageToleranceSeconds
            : PositiveInt(
                section["GameServiceOutageToleranceSeconds"],
                "SimulationWorker:GameServiceOutageToleranceSeconds",
                errors);
        var uncertainOperationRetryHorizonSeconds = string.IsNullOrWhiteSpace(
                section["UncertainOperationRetryHorizonSeconds"])
            ? DefaultUncertainOperationRetryHorizonSeconds
            : PositiveInt(
                section["UncertainOperationRetryHorizonSeconds"],
                "SimulationWorker:UncertainOperationRetryHorizonSeconds",
                errors);
        var networkMetricsLogSeconds = PositiveInt(
            section["NetworkMetricsLogSeconds"],
            "SimulationWorker:NetworkMetricsLogSeconds",
            errors);
        var performanceReportPath = OptionalFullPath(
            section["PerformanceReportPath"],
            "SimulationWorker:PerformanceReportPath",
            errors);
        var characterCheckpointSeconds = string.IsNullOrWhiteSpace(
                section["CharacterCheckpointSeconds"])
            ? 30
            : PositiveInt(
                section["CharacterCheckpointSeconds"],
                "SimulationWorker:CharacterCheckpointSeconds",
                errors);
        var lingeringBodySeconds = string.IsNullOrWhiteSpace(
                section["LingeringBodySeconds"])
            ? DefaultLingeringBodySeconds
            : PositiveInt(
                section["LingeringBodySeconds"],
                "SimulationWorker:LingeringBodySeconds",
                errors);
        var tickPhaseFailureLimit = string.IsNullOrWhiteSpace(
                section["TickPhaseFailureLimit"])
            ? DefaultTickPhaseFailureLimit
            : PositiveInt(
                section["TickPhaseFailureLimit"],
                "SimulationWorker:TickPhaseFailureLimit",
                errors);
        var udpQuotaSection = section.GetSection("UdpQuotas");
        var inboundPacketsPerSecond = PositiveInt(
            udpQuotaSection["InboundPacketsPerSecond"],
            "SimulationWorker:UdpQuotas:InboundPacketsPerSecond",
            errors);
        var inboundPacketBurst = PositiveInt(
            udpQuotaSection["InboundPacketBurst"],
            "SimulationWorker:UdpQuotas:InboundPacketBurst",
            errors);
        var inboundBytesPerSecond = PositiveInt(
            udpQuotaSection["InboundBytesPerSecond"],
            "SimulationWorker:UdpQuotas:InboundBytesPerSecond",
            errors);
        var inboundByteBurst = PositiveInt(
            udpQuotaSection["InboundByteBurst"],
            "SimulationWorker:UdpQuotas:InboundByteBurst",
            errors);
        var snapshotBytesPerSecond = PositiveInt(
            udpQuotaSection["SnapshotBytesPerSecond"],
            "SimulationWorker:UdpQuotas:SnapshotBytesPerSecond",
            errors);
        var snapshotByteBurst = PositiveInt(
            udpQuotaSection["SnapshotByteBurst"],
            "SimulationWorker:UdpQuotas:SnapshotByteBurst",
            errors);
        var aggregateSnapshotBytesPerSecond = PositiveInt(
            udpQuotaSection["AggregateSnapshotBytesPerSecond"],
            "SimulationWorker:UdpQuotas:AggregateSnapshotBytesPerSecond",
            errors);
        var aggregateSnapshotByteBurst = PositiveInt(
            udpQuotaSection["AggregateSnapshotByteBurst"],
            "SimulationWorker:UdpQuotas:AggregateSnapshotByteBurst",
            errors);
        var admissionSection = section.GetSection("Admission");
        var admissionDefaults = AdmissionConfig.Default;
        var maxUnauthenticatedPeers = PositiveInt(
            admissionSection["MaxUnauthenticatedPeers"]
                ?? admissionDefaults.MaxUnauthenticatedPeers.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:Admission:MaxUnauthenticatedPeers",
            errors);
        var maxUnauthenticatedPeersPerAddress = PositiveInt(
            admissionSection["MaxUnauthenticatedPeersPerAddress"]
                ?? admissionDefaults.MaxUnauthenticatedPeersPerAddress.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:Admission:MaxUnauthenticatedPeersPerAddress",
            errors);
        var connectionAttemptsPerAddressPerSecond = PositiveInt(
            admissionSection["ConnectionAttemptsPerAddressPerSecond"]
                ?? admissionDefaults.ConnectionAttemptsPerAddressPerSecond.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:Admission:ConnectionAttemptsPerAddressPerSecond",
            errors);
        var connectionAttemptBurstPerAddress = PositiveInt(
            admissionSection["ConnectionAttemptBurstPerAddress"]
                ?? admissionDefaults.ConnectionAttemptBurstPerAddress.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:Admission:ConnectionAttemptBurstPerAddress",
            errors);
        var maxReliableQueuedPackets = PositiveInt(
            admissionSection["MaxReliableQueuedPackets"]
                ?? admissionDefaults.MaxReliableQueuedPackets.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:Admission:MaxReliableQueuedPackets",
            errors);
        var interestSection = section.GetSection("InterestManagement");
        var interestCellSize = FiniteFloat(
            interestSection["CellSize"],
            "SimulationWorker:InterestManagement:CellSize",
            errors);
        var interestEnterRadius = FiniteFloat(
            interestSection["EnterRadius"],
            "SimulationWorker:InterestManagement:EnterRadius",
            errors);
        var interestExitRadius = FiniteFloat(
            interestSection["ExitRadius"],
            "SimulationWorker:InterestManagement:ExitRadius",
            errors);
        var interestRefreshIntervalSnapshots = PositiveInt(
            interestSection["RefreshIntervalSnapshots"]
                ?? InterestManagementConfig.Default.RefreshIntervalSnapshots.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:InterestManagement:RefreshIntervalSnapshots",
            errors);
        var interestNearRadius = FiniteFloat(
            interestSection["NearRadius"]
                ?? InterestManagementConfig.Default.NearRadius.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:InterestManagement:NearRadius",
            errors);
        var interestNearExitRadius = FiniteFloat(
            interestSection["NearExitRadius"]
                ?? InterestManagementConfig.Default.NearExitRadius.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:InterestManagement:NearExitRadius",
            errors);
        var snapshotReplicationSection = section.GetSection("SnapshotReplication");
        var replicationDefaults = SnapshotReplicationConfig.Default;
        var overloadTargetUtilizationBasisPoints = PositiveInt(
            snapshotReplicationSection["OverloadTargetUtilizationBasisPoints"] ?? "9500",
            "SimulationWorker:SnapshotReplication:OverloadTargetUtilizationBasisPoints",
            errors);
        var playerBytesPerSecond = PositiveInt(
            snapshotReplicationSection["PlayerBytesPerSecond"]
                ?? replicationDefaults.PlayerBytesPerSecond.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:SnapshotReplication:PlayerBytesPerSecond",
            errors);
        var farIntervalSnapshots = PositiveInt(
            snapshotReplicationSection["FarIntervalSnapshots"]
                ?? replicationDefaults.FarIntervalSnapshots.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:SnapshotReplication:FarIntervalSnapshots",
            errors);
        var stopRepeats = PositiveInt(
            snapshotReplicationSection["StopRepeats"]
                ?? replicationDefaults.StopRepeats.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:SnapshotReplication:StopRepeats",
            errors);
        var refreshIntervalSnapshots = PositiveInt(
            snapshotReplicationSection["RefreshIntervalSnapshots"]
                ?? replicationDefaults.RefreshIntervalSnapshots.ToString(CultureInfo.InvariantCulture),
            "SimulationWorker:SnapshotReplication:RefreshIntervalSnapshots",
            errors);
        var collisionStreamingSection = section.GetSection("CollisionStreaming");
        var collisionLoadRadiusChunks = PositiveInt(
            collisionStreamingSection["LoadRadiusChunks"],
            "SimulationWorker:CollisionStreaming:LoadRadiusChunks",
            errors);
        var collisionUnloadRadiusChunks = PositiveInt(
            collisionStreamingSection["UnloadRadiusChunks"],
            "SimulationWorker:CollisionStreaming:UnloadRadiusChunks",
            errors);
        var healthTimeoutMilliseconds = PositiveInt(
            configuration["HealthChecks:TimeoutMilliseconds"],
            "HealthChecks:TimeoutMilliseconds",
            errors);

        if (udpPort > 65535)
        {
            errors.Add("SimulationWorker:UdpPort must be between 1 and 65535.");
        }

        if (advertisedUdpPort > 65535)
        {
            errors.Add("SimulationWorker:AdvertisedUdpPort must be between 1 and 65535.");
        }

        if (advertisedHost is not null
            && (advertisedHost.Length > 253
                || advertisedHost.Any(char.IsWhiteSpace)
                || advertisedHost.Contains("/", StringComparison.Ordinal)
                || advertisedHost.Contains("\\", StringComparison.Ordinal)))
        {
            errors.Add("SimulationWorker:AdvertisedHost must be a host name or IP address without a scheme or path.");
        }

        ValidateIdentifier(
            simulationWorkerId,
            "SimulationWorker:SimulationWorkerId",
            errors);
        ValidateIdentifier(fleetId, "SimulationWorker:FleetId", errors);
        ValidateIdentifier(nodeId, "SimulationWorker:NodeId", errors);
        ValidateIdentifier(shardId, "SimulationWorker:ShardId", errors);
        ValidateIdentifier(worldId, "SimulationWorker:WorldId", errors);

        if (maxConnections > 10_000)
        {
            errors.Add("SimulationWorker:MaxConnections must not exceed 10000.");
        }

        if (networkPollIntervalMilliseconds > 1000)
        {
            errors.Add("SimulationWorker:NetworkPollIntervalMilliseconds must not exceed 1000.");
        }

        if (sessionHeartbeatMaxConcurrency > 128)
        {
            errors.Add("SimulationWorker:SimulationSessionHeartbeatMaxConcurrency must not exceed 128.");
        }

        if (sessionHeartbeatBatchSize > MaximumSimulationSessionHeartbeatBatchSize)
        {
            errors.Add("SimulationWorker:SimulationSessionHeartbeatBatchSize must not exceed 500.");
        }

        if (sessionHeartbeatRetryMilliseconds >= sessionHeartbeatSeconds * 1_000L)
        {
            errors.Add(
                "SimulationWorker:SimulationSessionHeartbeatRetryMilliseconds must be shorter than SimulationSessionHeartbeatSeconds.");
        }

        if (registryAcquireTimeoutSeconds <= registryHeartbeatSeconds
            || registryAcquireTimeoutSeconds > 3_600)
        {
            errors.Add(
                "SimulationWorker:RegistryAcquireTimeoutSeconds must be longer than RegistryHeartbeatSeconds and must not exceed 3600.");
        }

        if (registryHeartbeatRetryMilliseconds >= registryHeartbeatSeconds * 1_000L)
        {
            errors.Add(
                "SimulationWorker:RegistryHeartbeatRetryMilliseconds must be shorter than RegistryHeartbeatSeconds.");
        }

        if (gameServiceOutageToleranceSeconds is < MinimumGameServiceOutageToleranceSeconds
            or > MaximumGameServiceOutageToleranceSeconds)
        {
            errors.Add(
                $"SimulationWorker:GameServiceOutageToleranceSeconds must be between {MinimumGameServiceOutageToleranceSeconds} and {MaximumGameServiceOutageToleranceSeconds}.");
        }

        if (uncertainOperationRetryHorizonSeconds
                < gameServiceOutageToleranceSeconds + MinimumUncertainOperationRetryHorizonMarginSeconds
            || uncertainOperationRetryHorizonSeconds > MaximumUncertainOperationRetryHorizonSeconds)
        {
            errors.Add(
                $"SimulationWorker:UncertainOperationRetryHorizonSeconds must be at least GameServiceOutageToleranceSeconds plus {MinimumUncertainOperationRetryHorizonMarginSeconds} and must not exceed {MaximumUncertainOperationRetryHorizonSeconds}.");
        }

        if (networkMetricsLogSeconds > 3_600)
        {
            errors.Add("SimulationWorker:NetworkMetricsLogSeconds must not exceed 3600.");
        }

        if (characterCheckpointSeconds > 3_600)
        {
            errors.Add("SimulationWorker:CharacterCheckpointSeconds must not exceed 3600.");
        }

        if (lingeringBodySeconds > MaximumLingeringBodySeconds)
        {
            errors.Add("SimulationWorker:LingeringBodySeconds must not exceed 3600.");
        }

        if (inboundPacketBurst < inboundPacketsPerSecond
            || inboundByteBurst < inboundBytesPerSecond
            || snapshotByteBurst < snapshotBytesPerSecond)
        {
            errors.Add("SimulationWorker UDP quota burst values must be greater than or equal to their sustained rates.");
        }

        if (inboundPacketsPerSecond > 10_000
            || inboundBytesPerSecond > 16 * 1024 * 1024
            || snapshotBytesPerSecond > 16 * 1024 * 1024
            || inboundPacketBurst > 20_000
            || inboundByteBurst > 32 * 1024 * 1024
            || snapshotByteBurst > 32 * 1024 * 1024)
        {
            errors.Add("SimulationWorker UDP quota rates or bursts exceed the supported safety limits.");
        }

        if (aggregateSnapshotBytesPerSecond > 256 * 1024 * 1024
            || aggregateSnapshotByteBurst > 64 * 1024 * 1024)
        {
            errors.Add("SimulationWorker aggregate snapshot quota exceeds the supported safety limits.");
        }

        if (interestCellSize <= 0f
            || interestEnterRadius <= 0f
            || interestExitRadius < interestEnterRadius
            || interestCellSize > 4_096f
            || interestExitRadius > 10_000f
            || interestExitRadius / interestCellSize > 64f)
        {
            errors.Add("SimulationWorker interest management requires bounded positive radii, a positive cell size, an exit radius greater than or equal to the enter radius, and no more than 64 searched cells per axis direction.");
        }

        if (interestRefreshIntervalSnapshots > snapshotRateHz)
        {
            errors.Add(
                "SimulationWorker:InterestManagement:RefreshIntervalSnapshots must not exceed one second of snapshots.");
        }

        if (interestNearRadius <= 0f
            || interestNearExitRadius < interestNearRadius
            || interestNearExitRadius > interestEnterRadius)
        {
            errors.Add(
                "SimulationWorker:InterestManagement:NearRadius must be positive, and NearExitRadius at least NearRadius and at most EnterRadius.");
        }

        if (overloadTargetUtilizationBasisPoints > 10_000)
        {
            errors.Add(
                "SimulationWorker:SnapshotReplication:OverloadTargetUtilizationBasisPoints must not exceed 10000.");
        }

        if (playerBytesPerSecond < RealtimeProtocol.OwnerSimulationSnapshotPacketSize * snapshotRateHz * 2
            || playerBytesPerSecond > snapshotBytesPerSecond)
        {
            errors.Add(
                "SimulationWorker:SnapshotReplication:PlayerBytesPerSecond must hold at least twice the owner snapshots of one second and at most UdpQuotas:SnapshotBytesPerSecond.");
        }

        if (farIntervalSnapshots > snapshotRateHz
            || stopRepeats > snapshotRateHz
            || refreshIntervalSnapshots > 10 * snapshotRateHz
            || refreshIntervalSnapshots < farIntervalSnapshots)
        {
            errors.Add(
                "SimulationWorker:SnapshotReplication:FarIntervalSnapshots and StopRepeats must not exceed one second of snapshots, and RefreshIntervalSnapshots must lie between FarIntervalSnapshots and ten seconds of snapshots.");
        }

        if (collisionUnloadRadiusChunks < collisionLoadRadiusChunks)
        {
            errors.Add("SimulationWorker:CollisionStreaming:UnloadRadiusChunks must be greater than or equal to LoadRadiusChunks.");
        }

        if (collisionUnloadRadiusChunks > 16)
        {
            errors.Add("SimulationWorker:CollisionStreaming:UnloadRadiusChunks must not exceed 16.");
        }

        if (snapshotRateHz > movementTickRateHz
            || movementTickRateHz % snapshotRateHz != 0)
        {
            errors.Add(
                "SimulationWorker:Movement:SnapshotRateHz must divide TickRateHz without a remainder.");
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
                rotationSpeed,
                gravity,
                maximumFallSpeed,
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
            errors.Add($"SimulationWorker:Movement is invalid: {exception.Message}");
        }

        var itemInteraction = ItemInteractionConfig.FromConfiguration(
            section.GetSection("ItemInteraction"),
            errors);

        if (movementSimulation is not null
            && (spawnX < movementSimulation.MinimumX
                || spawnX > movementSimulation.MaximumX
                || spawnY < movementSimulation.GroundHeight
                || spawnZ < movementSimulation.MinimumZ
                || spawnZ > movementSimulation.MaximumZ))
        {
            errors.Add("The selected World manifest spawn must be inside its movement bounds.");
        }

        var parsedGameServiceUrl = ParseHttpUri(gameServiceUrl, "SimulationWorker:GameServiceBaseUrl", errors);

        if (gameServiceSecret is not null && gameServiceSecret.Length < 32)
        {
            errors.Add("SimulationWorker:GameServiceSecret must be at least 32 characters.");
        }
        else if (gameServiceSecret is not null
            && gameServiceSecret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase))
        {
            // Every secret placeholder in .env.example starts with this prefix.
            errors.Add("SimulationWorker:GameServiceSecret still has the .env.example placeholder value.");
        }

        if (redis is not null)
        {
            try
            {
                Mmorpg.Server.Common.Networking.RedisConnectionString.ParseRequiredEndpoint(redis);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add($"ConnectionStrings:Redis is invalid: {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "SimulationWorker configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new SimulationWorkerConfig(
            simulationWorkerId!,
            fleetId!,
            nodeId!,
            shardId!,
            worldId!,
            worldDataPath!,
            udpPort,
            advertisedHost!,
            advertisedUdpPort,
            maxConnections,
            TimeSpan.FromSeconds(joinHandshakeTimeoutSeconds),
            TimeSpan.FromMilliseconds(networkPollIntervalMilliseconds),
            snapshotRateHz,
            movementSimulation!,
            new MovementSpawnConfig(spawnX, spawnY, spawnZ, spawnYaw),
            parsedGameServiceUrl!,
            TimeSpan.FromSeconds(authTimeoutSeconds),
            gameServiceSecret!,
            TimeSpan.FromSeconds(sessionHeartbeatSeconds),
            TimeSpan.FromSeconds(registryHeartbeatSeconds),
            redis!,
            TimeSpan.FromMilliseconds(healthTimeoutMilliseconds))
        {
            SimulationSessionHeartbeatMaxConcurrency = sessionHeartbeatMaxConcurrency,
            SimulationSessionHeartbeatBatchSize = sessionHeartbeatBatchSize,
            SimulationSessionHeartbeatRetryDelay = TimeSpan.FromMilliseconds(sessionHeartbeatRetryMilliseconds),
            RegistryAcquireTimeout = TimeSpan.FromSeconds(registryAcquireTimeoutSeconds),
            RegistryHeartbeatRetryDelay = TimeSpan.FromMilliseconds(registryHeartbeatRetryMilliseconds),
            GameServiceOutageTolerance = TimeSpan.FromSeconds(gameServiceOutageToleranceSeconds),
            UncertainOperationRetryHorizon = TimeSpan.FromSeconds(uncertainOperationRetryHorizonSeconds),
            NetworkMetricsLogInterval = TimeSpan.FromSeconds(networkMetricsLogSeconds),
            PerformanceReportPath = performanceReportPath,
            CharacterCheckpointInterval = TimeSpan.FromSeconds(characterCheckpointSeconds),
            LingeringBodySeconds = lingeringBodySeconds,
            TickPhaseFailureLimit = tickPhaseFailureLimit,
            UdpQuotas = new UdpQuotaConfig(
                inboundPacketsPerSecond,
                inboundPacketBurst,
                inboundBytesPerSecond,
                inboundByteBurst,
                snapshotBytesPerSecond,
                snapshotByteBurst,
                aggregateSnapshotBytesPerSecond,
                aggregateSnapshotByteBurst),
            Admission = new AdmissionConfig(
                maxUnauthenticatedPeers,
                maxUnauthenticatedPeersPerAddress,
                connectionAttemptsPerAddressPerSecond,
                connectionAttemptBurstPerAddress,
                maxReliableQueuedPackets),
            InterestManagement = new InterestManagementConfig(
                interestCellSize,
                interestEnterRadius,
                interestExitRadius,
                interestRefreshIntervalSnapshots,
                interestNearRadius,
                interestNearExitRadius),
            SnapshotReplication = new SnapshotReplicationConfig(
                overloadTargetUtilizationBasisPoints,
                playerBytesPerSecond,
                farIntervalSnapshots,
                stopRepeats,
                refreshIntervalSnapshots),
            CollisionStreaming = new CollisionStreamingConfig(
                collisionLoadRadiusChunks,
                collisionUnloadRadiusChunks),
            ItemInteraction = itemInteraction,
            ActorCatalogPath = ResolveOutputPath(section["ActorCatalogPath"], DefaultActorCatalogPath),
            ActorSpawnsPath = WorldManifestFileStore.ResolveActorSpawnsPath(
                worldDataPath!,
                worldId!),
            ItemCatalogPath = ResolveOutputPath(section["ItemCatalogPath"], DefaultItemCatalogPath),
            QuestCatalogPath = ResolveOutputPath(section["QuestCatalogPath"], DefaultQuestCatalogPath),
            CombatRulesPath = ResolveOutputPath(section["CombatRulesPath"], DefaultCombatRulesPath),
            CharacterAppearancePath = ResolveOutputPath(section["CharacterAppearancePath"], DefaultCharacterAppearancePath)
        };
    }

    // A relative path is below the worker's output, where the build copies the content.
    private static string ResolveOutputPath(string? configuredPath, string defaultPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultPath
            : configuredPath.Trim();
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string? OptionalFullPath(string? value, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{key} must be a valid file path.");
            return null;
        }
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

    private static void ValidateIdentifier(
        string? value,
        string key,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > 128
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            errors.Add($"{key} must be a valid identifier.");
        }
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
    int SnapshotByteBurst,
    int AggregateSnapshotBytesPerSecond,
    int AggregateSnapshotByteBurst)
{
    public static UdpQuotaConfig Default { get; } = new(
        120,
        240,
        128 * 1024,
        256 * 1024,
        256 * 1024,
        512 * 1024,
        38 * 1024 * 1024,
        4 * 1024 * 1024);
}

public sealed record AdmissionConfig(
    int MaxUnauthenticatedPeers,
    int MaxUnauthenticatedPeersPerAddress,
    int ConnectionAttemptsPerAddressPerSecond,
    int ConnectionAttemptBurstPerAddress,
    int MaxReliableQueuedPackets)
{
    public static AdmissionConfig Default { get; } = new(256, 8, 5, 20, 1024);
}

// RefreshIntervalSnapshots spreads the visibility work: each connection's visible set is
// recomputed every that many snapshots, the connections in turn. The exit radius beyond the
// enter radius keeps an entity visible while its viewer waits for the next refresh. A visible
// entity within NearRadius becomes near and stays near until it is beyond NearExitRadius;
// the refresh decides it.
public sealed record InterestManagementConfig(
    float CellSize,
    float EnterRadius,
    float ExitRadius,
    int RefreshIntervalSnapshots = 1,
    float NearRadius = 48f,
    float NearExitRadius = 56f)
{
    public static InterestManagementConfig Default { get; } = new(64f, 128f, 144f, 3);
}

// How remote records are scheduled: a recipient's byte budget per second, the snapshots
// between two records of a far entity that changed, the repeats of a record that stopped
// changing, and the snapshots between two records of an entity that stands.
public sealed record SnapshotReplicationConfig(
    int OverloadTargetUtilizationBasisPoints,
    int PlayerBytesPerSecond = 32_768,
    int FarIntervalSnapshots = 3,
    int StopRepeats = 3,
    int RefreshIntervalSnapshots = 15)
{
    public static SnapshotReplicationConfig Default { get; } = new(9_500);
}

public sealed record CollisionStreamingConfig(
    int LoadRadiusChunks,
    int UnloadRadiusChunks)
{
    public static CollisionStreamingConfig Default { get; } = new(2, 3);
}
