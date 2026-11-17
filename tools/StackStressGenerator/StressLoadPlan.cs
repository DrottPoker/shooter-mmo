using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mmorpg.Tools.NetworkImpairmentProxy;

namespace Mmorpg.Tools.StackStressGenerator;

// A load scenario of tools/load/scenarios: the stack boundary, the workload, the World and
// Shard, the generator arguments, and extra worker and GameService settings. Adding a scenario
// needs no code change; an unknown property, generator option, or reserved setting is an error.
public sealed record StressLoadScenario(
    string Name,
    string Description,
    StressRunMode Mode,
    StressWorkloadProfile Workload,
    string ShardId,
    string WorldId,
    IReadOnlyDictionary<string, int> Generator,
    IReadOnlyDictionary<string, string> WorkerSettings,
    IReadOnlyDictionary<string, string> GameServiceSettings)
{
    // The file the scenario came from, which the playing bots read again.
    public string? SourcePath { get; init; }

    // The roles and home grounds of the playing workload.
    public PlayingScenario? Playing { get; init; }

    // The extra Mobs and nodes of a load variant of the World, or null for the checked-in World.
    public WorldVariantScenario? WorldVariant { get; init; }

    // The network profile of tools/load/network-profiles the bots' traffic goes through, or null
    // for a direct connection; -Network of tools/Test-Load.ps1 overrides it.
    public string? Network { get; init; }

    // The GameService outage that tools/Test-Load.ps1 makes during the steady interval, or null.
    public StressOutageScenario? Outage { get; init; }

    // The generator options a scenario may set. Endpoints, identities, secrets, and output paths
    // belong to the plan.
    public static readonly IReadOnlySet<string> GeneratorOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "bots",
        "bot-start-index",
        "ramp-step",
        "ramp-interval-seconds",
        "duration-seconds",
        "worker-wait-seconds",
        "join-timeout-seconds",
        "http-timeout-seconds",
        "http-concurrency",
        "session-validation-requests-per-bot",
        "inventory-operation-interval-seconds",
        "loot-operation-interval-seconds",
        "gameplay-operation-timeout-seconds",
        "loot-hotspot-x",
        "loot-hotspot-y",
        "loot-hotspot-z",
        "report-interval-seconds",
        "seed",
        "poll-threads",
        "login-burst-accounts",
        "logout-burst-accounts",
        "burst-at-seconds",
        "burst-deadline-seconds"
    };

    private static readonly Regex SettingName = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public static StressLoadScenario Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StressGeneratorOptionException($"The scenario '{path}' cannot be read: {exception.Message}");
        }

        return Parse(Path.GetFileNameWithoutExtension(path), text, path) with { SourcePath = Path.GetFullPath(path) };
    }

    public static StressLoadScenario Parse(string name, string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new StressGeneratorOptionException($"'{source}' is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            StrictJson.RequireObject(root, source, "the scenario");
            StrictJson.RequireOnly(
                root,
                source,
                "the scenario",
                "description",
                "mode",
                "workload",
                "shardId",
                "worldId",
                "generator",
                "workerSettings",
                "gameServiceSettings",
                "playing",
                "worldVariant",
                "network",
                "outage");
            var mode = StrictJson.RequireString(root, "mode", source, "the scenario") switch
            {
                "worker-only" => StressRunMode.WorkerOnly,
                "full-stack" => StressRunMode.FullStack,
                var other => throw new StressGeneratorOptionException(
                    $"'{source}': the mode '{other}' is neither worker-only nor full-stack.")
            };
            var workload = StrictJson.RequireString(root, "workload", source, "the scenario") switch
            {
                "lifecycle" => StressWorkloadProfile.Lifecycle,
                "inventory" => StressWorkloadProfile.Inventory,
                "loot-hotspot" => StressWorkloadProfile.LootHotspot,
                "mixed-gameplay" => StressWorkloadProfile.MixedGameplay,
                "playing" => StressWorkloadProfile.Playing,
                var other => throw new StressGeneratorOptionException(
                    $"'{source}': the workload '{other}' is unknown.")
            };
            PlayingScenario? playing = null;
            if (root.TryGetProperty("playing", out var playingElement))
            {
                if (workload != StressWorkloadProfile.Playing)
                {
                    throw new StressGeneratorOptionException(
                        $"'{source}': only the playing workload takes a 'playing' section.");
                }

                playing = PlayingScenario.Parse(playingElement, source);
            }
            else if (workload == StressWorkloadProfile.Playing)
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': the playing workload needs a 'playing' section.");
            }

            var worldVariant = root.TryGetProperty("worldVariant", out var variantElement)
                ? WorldVariantScenario.Parse(variantElement, source)
                : null;
            if (worldVariant is not null && mode != StressRunMode.FullStack)
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': a World variant needs full-stack mode.");
            }
            if (mode == StressRunMode.WorkerOnly && workload != StressWorkloadProfile.Lifecycle)
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': worker-only mode runs only the lifecycle workload.");
            }

            string? network = null;
            if (root.TryGetProperty("network", out var networkElement))
            {
                network = networkElement.ValueKind == JsonValueKind.String ? networkElement.GetString() : null;
                if (network is null || (network != NetworkProfile.None && !NetworkProfile.IsValidName(network)))
                {
                    throw new StressGeneratorOptionException(
                        $"'{source}': 'network' must name a profile of tools/load/network-profiles or be '{NetworkProfile.None}'.");
                }
            }

            StressOutageScenario? outage = null;
            if (root.TryGetProperty("outage", out var outageElement))
            {
                if (mode != StressRunMode.FullStack || workload != StressWorkloadProfile.Playing)
                {
                    throw new StressGeneratorOptionException(
                        $"'{source}': a GameService outage needs full-stack mode and the playing workload.");
                }

                outage = StressOutageScenario.Parse(outageElement, source);
            }

            var generator = ReadGenerator(root, source);
            var workerSettings = ReadSettings(root, "workerSettings", source);
            var gameServiceSettings = ReadSettings(root, "gameServiceSettings", source);
            if (mode == StressRunMode.WorkerOnly && gameServiceSettings.Count > 0)
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': worker-only mode starts no GameService, so it takes no gameServiceSettings.");
            }

            return new StressLoadScenario(
                name,
                StrictJson.RequireString(root, "description", source, "the scenario"),
                mode,
                workload,
                StrictJson.RequireString(root, "shardId", source, "the scenario"),
                StrictJson.RequireString(root, "worldId", source, "the scenario"),
                generator,
                workerSettings,
                gameServiceSettings)
            {
                Playing = playing,
                WorldVariant = worldVariant,
                Network = network,
                Outage = outage
            };
        }
    }

    private static Dictionary<string, int> ReadGenerator(JsonElement root, string source)
    {
        if (!root.TryGetProperty("generator", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new StressGeneratorOptionException($"'{source}': the scenario needs the object 'generator'.");
        }

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!GeneratorOptions.Contains(property.Name))
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': the generator option '{property.Name}' is unknown or set by the plan.");
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': the generator option '{property.Name}' must be an integer.");
            }

            values[property.Name] = value;
        }

        foreach (var required in new[] { "bots", "duration-seconds" })
        {
            if (!values.ContainsKey(required))
            {
                throw new StressGeneratorOptionException($"'{source}': the generator option '{required}' is required.");
            }
        }

        return values;
    }

    private static Dictionary<string, string> ReadSettings(JsonElement root, string name, string source)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var element))
        {
            return settings;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StressGeneratorOptionException($"'{source}': '{name}' must be an object.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!SettingName.IsMatch(property.Name))
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': '{property.Name}' in '{name}' is not an environment variable name.");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': the value of '{property.Name}' in '{name}' must be a string.");
            }

            settings[property.Name] = property.Value.GetString()!;
        }

        return settings;
    }
}

// What tools/Test-Load.ps1 starts and runs for one scenario: the environment of each process,
// the generator arguments, the ports, and the files of the run folder. The local test values
// below are the ones docs/STACK_STRESS_TESTING.md shows.
public sealed record StressLoadPlan(
    string Scenario,
    string Description,
    string Mode,
    string Workload,
    bool NeedsContainers,
    bool NeedsGameService,
    string? GameServiceUrl,
    string AuthorityUrl,
    string ShardId,
    int WorkerUdpPort,
    IReadOnlyList<int> TcpPorts,
    IReadOnlyList<int> ContainerTcpPorts,
    IReadOnlyList<int> UdpPorts,
    IReadOnlyDictionary<string, string> GameServiceEnvironment,
    IReadOnlyDictionary<string, string> WorkerEnvironment,
    IReadOnlyDictionary<string, string> GeneratorEnvironment,
    IReadOnlyList<string> GeneratorArguments,
    IReadOnlyList<string> Sensitive,
    string ReportPath,
    string SummaryPath,
    string WorkerPerformancePath)
{
    // The folder the World variant goes to, which tools/Test-Load.ps1 writes with
    // StackStressGenerator world-variant before the worker starts, or null for none.
    public string? WorldVariantPath { get; init; }

    // The network profile of the run, 'none' for a direct connection, and the proxy that
    // tools/Test-Load.ps1 starts between the bots and the worker for any other.
    public string Network { get; init; } = NetworkProfile.None;

    public string? NetworkProfilePath { get; init; }

    public StressNetworkProxyPlan? Proxy { get; init; }

    // The GameService outage that tools/Test-Load.ps1 makes, with the files it exchanges with the
    // generator, or null for none.
    public StressOutagePlan? Outage { get; init; }

    // The console format of both services in a run with an outage: every entry starts with its
    // UTC time, so the judge can tell the outage's own log lines from the others. The console
    // logger reads the formatter options only for a formatter named explicitly.
    public const string LogFormatterSetting = "Logging__Console__FormatterName";
    public const string LogTimestampFormatSetting = "Logging__Console__FormatterOptions__TimestampFormat";
    public const string LogTimestampUtcSetting = "Logging__Console__FormatterOptions__UseUtcTimestamp";
    public const string LogTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";

    public const string FullStackGameServiceUrl = "http://127.0.0.1:5500";
    public const int FullStackGameServicePort = 5500;
    public const int FullStackWorkerUdpPort = 27025;
    public const string WorkerOnlyAuthorityUrl = "http://127.0.0.1:5099";
    public const int WorkerOnlyAuthorityPort = 5099;
    public const int WorkerOnlyUdpPort = 27016;
    public const int PostgresPort = 56432;
    public const int RedisPort = 56379;
    public const string Database = "shooter_mmo_stack_stress_test";
    public const string WorkerId = "local-simulation-worker-2";
    public const string MetricsIntervalSetting = "SimulationWorker__NetworkMetricsLogSeconds";
    public const string WorldVariantFolderName = "world-variant";

    // The tick rules judge only worker intervals that lie entirely inside the steady interval,
    // and the reporter is not synchronized with it, so the steady interval holds this many whole
    // worker intervals at least.
    public const int MinimumSteadyWorkerIntervals = 3;
    public const int LongestMetricsIntervalSeconds = 10;

    // The worker interval for a steady interval: 10 seconds, shorter for a short run, so that
    // one more interval than the minimum fits and a partial one at each end does not matter.
    public static int MetricsIntervalSeconds(int steadySeconds)
    {
        return Math.Clamp(steadySeconds / (MinimumSteadyWorkerIntervals + 1), 1, LongestMetricsIntervalSeconds);
    }

    private const string PostgresConnection =
        "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only";
    private const string RedisConnection = "127.0.0.1:56379";
    private const string FullStackWorkerSecret = "stack-stress-local-worker-secret-2026";
    private const string FixtureSecret = "stack-stress-local-fixture-secret-2026";

    public static readonly IReadOnlyList<string> SensitiveNames =
    [
        "ConnectionStrings__Postgres",
        "SIMULATION_WORKER_SERVICE_SECRET",
        StressGeneratorOptions.FixtureSecretEnvironmentVariable,
        StressGeneratorOptions.PostgresConnectionEnvironmentVariable,
        "--worker-secret"
    ];

    public static StressLoadPlan Create(
        StressLoadScenario scenario,
        StressLoadPlanRequest request,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(request);
        var generator = new Dictionary<string, int>(scenario.Generator, StringComparer.Ordinal);
        if (request.Bots is { } bots)
        {
            generator["bots"] = bots;
        }

        if (request.DurationSeconds is { } duration)
        {
            generator["duration-seconds"] = duration;
        }

        // An override of the bot count keeps every ramp step within it.
        if (generator.TryGetValue("ramp-step", out var rampStep) && rampStep > generator["bots"])
        {
            generator["ramp-step"] = generator["bots"];
        }

        var runFolder = Path.GetFullPath(request.RunFolder, workingDirectory);
        var reportPath = Path.Combine(runFolder, "generator-report.json");
        var summaryPath = Path.Combine(runFolder, "summary.json");
        var performancePath = Path.Combine(runFolder, "worker-performance.jsonl");
        var fullStack = scenario.Mode == StressRunMode.FullStack;
        var gameplay = scenario.Workload != StressWorkloadProfile.Lifecycle;
        var worldVariantPath = scenario.WorldVariant is null
            ? null
            : Path.Combine(runFolder, WorldVariantFolderName);

        // A network profile puts the proxy on the port the worker advertises, so every bot joins
        // through it, and the worker keeps listening on its own port behind it.
        var networkName = request.Network ?? scenario.Network ?? NetworkProfile.None;
        string? networkProfilePath = null;
        NetworkProfile? networkProfile = null;
        if (networkName != NetworkProfile.None)
        {
            var repositoryRoot = Mmorpg.WorldData.Content.ContentLayout.FindRepositoryRoot(workingDirectory);
            networkProfile = StressNetworkProfiles.LoadNamed(networkName, repositoryRoot);
            networkProfilePath = Path.Combine(StressNetworkProfiles.FolderPath(repositoryRoot), networkName + ".json");
        }

        var gameServiceEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var workerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var generatorEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = new List<string>();
        var udpPort = fullStack ? FullStackWorkerUdpPort : WorkerOnlyUdpPort;
        var advertisedPort = networkProfile is null ? udpPort : udpPort + StressNetworkProfiles.ProxyPortOffset;
        if (fullStack)
        {
            // The disposable stack uses the published passwords of docker-compose.stack-stress.yml,
            // a Redis without a password, and the shared worker secret, which GameService
            // accepts only in the Development environment, so every full-stack run uses it.
            gameServiceEnvironment["DOTNET_ENVIRONMENT"] = "Development";
            gameServiceEnvironment["ConnectionStrings__Postgres"] = PostgresConnection;
            gameServiceEnvironment["ConnectionStrings__Redis"] = RedisConnection;
            // The disposable database starts empty; the Development settings migrate it at
            // the start already, and this keeps it so whatever they say.
            gameServiceEnvironment["Database__RunMigrationsOnStartup"] = "true";
            gameServiceEnvironment["SIMULATION_WORKER_SERVICE_SECRET"] = FullStackWorkerSecret;
            gameServiceEnvironment["DevelopmentSimulationBots__Enabled"] = "false";
            // Covers the registration and login of every bot within one minute.
            gameServiceEnvironment["RateLimiting__Authentication__PermitLimit"] = "1000";
            gameServiceEnvironment["RateLimiting__Authentication__WindowSeconds"] = "60";
            // Every bot reads the Shard list from the same address at its admission and at
            // each rejoin, far more than one player's address does.
            gameServiceEnvironment["RateLimiting__Public__PermitLimit"] = "100000";
            gameServiceEnvironment["RateLimiting__Public__WindowSeconds"] = "60";
            gameServiceEnvironment["ASPNETCORE_URLS"] = FullStackGameServiceUrl;
            if (gameplay)
            {
                gameServiceEnvironment["StackStressFixtures__Enabled"] = "true";
                gameServiceEnvironment[StressGeneratorOptions.FixtureSecretEnvironmentVariable] = FixtureSecret;
            }

            workerEnvironment["GAME_SERVICE_BASE_URL"] = FullStackGameServiceUrl;
            workerEnvironment["ConnectionStrings__Redis"] = RedisConnection;
            workerEnvironment["SIMULATION_WORKER_SERVICE_SECRET"] = FullStackWorkerSecret;
            workerEnvironment["SIMULATION_WORKER_ID"] = WorkerId;
            workerEnvironment["SIMULATION_SHARD_ID"] = scenario.ShardId;
            workerEnvironment["SIMULATION_WORLD_ID"] = scenario.WorldId;
            if (scenario.Playing?.TeleportToGrounds == true)
            {
                // The admin teleport needs DevelopmentAdmin:Enabled, which the worker allows only
                // in the Development environment. That environment also loads the worker's
                // appsettings.Development.json, so its other Development option stays off here.
                workerEnvironment["DOTNET_ENVIRONMENT"] = "Development";
                workerEnvironment["DevelopmentAdmin__Enabled"] = "true";
                workerEnvironment["DevelopmentItemInteractions__GlobalBankAndRecoveryAccess"] = "false";
            }

            if (worldVariantPath is not null)
            {
                workerEnvironment["SimulationWorker__WorldDataPath"] = StressWorldVariant.WorkerWorldsPath(worldVariantPath);
            }

            generatorEnvironment[StressGeneratorOptions.PostgresConnectionEnvironmentVariable] = PostgresConnection;
            if (gameplay)
            {
                generatorEnvironment[StressGeneratorOptions.FixtureSecretEnvironmentVariable] = FixtureSecret;
            }

            arguments.AddRange(
            [
                "--mode", "full-stack",
                "--workload", StressGeneratorOptions.FormatWorkload(scenario.Workload),
                "--game-service-url", FullStackGameServiceUrl,
                "--confirm-disposable-database", Database
            ]);
            if (scenario.Workload == StressWorkloadProfile.Playing)
            {
                arguments.AddRange(["--scenario-file", scenario.SourcePath
                    ?? throw new StressGeneratorOptionException(
                        $"The playing scenario '{scenario.Name}' must be loaded from a file.")]);
            }

            if (worldVariantPath is not null)
            {
                arguments.AddRange(["--world-data-path", StressWorldVariant.WorkerWorldsPath(worldVariantPath)]);
            }

            // The soak reads Redis and compares the drift checks and the retention rules with
            // GameService's own limits.
            var limits = GameServiceLimits(scenario, workingDirectory);
            arguments.AddRange(
            [
                "--redis-endpoint", RedisConnection,
                "--database-pool-size", limits.PoolSize.ToString(CultureInfo.InvariantCulture),
                "--worker-heartbeat-timeout-seconds", limits.HeartbeatTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                "--retention-item-history-seconds", limits.ItemHistorySeconds.ToString(CultureInfo.InvariantCulture),
                "--retention-interval-seconds", limits.RetentionIntervalSeconds.ToString(CultureInfo.InvariantCulture)
            ]);
        }
        else
        {
            var workerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            workerEnvironment["GAME_SERVICE_BASE_URL"] = WorkerOnlyAuthorityUrl + "/";
            workerEnvironment["SIMULATION_WORKER_SERVICE_SECRET"] = workerSecret;
            workerEnvironment["SIMULATION_WORKER_MAX_CONNECTIONS"] =
                Math.Max(100, generator["bots"]).ToString(CultureInfo.InvariantCulture);
            workerEnvironment["SIMULATION_WORKER_ID"] = WorkerId;
            workerEnvironment["SIMULATION_FLEET_ID"] = "local-fleet";
            workerEnvironment["SIMULATION_NODE_ID"] = "local-node-1";
            workerEnvironment["SIMULATION_SHARD_ID"] = scenario.ShardId;
            workerEnvironment["SIMULATION_WORLD_ID"] = scenario.WorldId;
            arguments.AddRange(
            [
                "--mode", "worker-only",
                "--workload", "lifecycle",
                "--authority-url", WorkerOnlyAuthorityUrl,
                "--worker-host", "127.0.0.1",
                "--worker-udp-port", advertisedPort.ToString(CultureInfo.InvariantCulture),
                "--fleet-id", "local-fleet",
                "--node-id", "local-node-1",
                "--worker-secret", workerSecret,
                "--authority-hold-seconds", "120"
            ]);
        }

        workerEnvironment["SIMULATION_WORKER_UDP_PORT"] = udpPort.ToString(CultureInfo.InvariantCulture);
        workerEnvironment["SIMULATION_WORKER_ADVERTISED_HOST"] = "127.0.0.1";
        workerEnvironment["SIMULATION_WORKER_ADVERTISED_UDP_PORT"] = advertisedPort.ToString(CultureInfo.InvariantCulture);
        workerEnvironment[MetricsIntervalSetting] =
            MetricsIntervalSeconds(generator["duration-seconds"]).ToString(CultureInfo.InvariantCulture);
        workerEnvironment["SimulationWorker__PerformanceReportPath"] = performancePath;
        StressOutagePlan? outagePlan = null;
        if (scenario.Outage is { } outage)
        {
            foreach (var environment in new[] { workerEnvironment, gameServiceEnvironment })
            {
                environment[LogFormatterSetting] = "simple";
                environment[LogTimestampFormatSetting] = LogTimestampFormat;
                environment[LogTimestampUtcSetting] = "true";
            }

            outagePlan = new StressOutagePlan(
                outage.AtSeconds,
                outage.Seconds,
                Path.Combine(runFolder, StressOutageOptions.RequestFileName),
                Path.Combine(runFolder, StressOutageOptions.WindowFileName));
            arguments.AddRange(
            [
                "--outage-at-seconds", outage.AtSeconds.ToString(CultureInfo.InvariantCulture),
                "--outage-seconds", outage.Seconds.ToString(CultureInfo.InvariantCulture),
                "--outage-folder", runFolder
            ]);
        }

        Merge(workerEnvironment, scenario.WorkerSettings, "workerSettings", scenario.Name);
        RequireSteadyWorkerIntervals(
            scenario.Name,
            generator["duration-seconds"],
            workerEnvironment[MetricsIntervalSetting]);
        Merge(gameServiceEnvironment, scenario.GameServiceSettings, "gameServiceSettings", scenario.Name);

        arguments.AddRange(
        [
            "--worker-id", WorkerId,
            "--shard-id", scenario.ShardId,
            "--world-id", scenario.WorldId
        ]);
        foreach (var option in generator.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("--" + option.Key);
            arguments.Add(option.Value.ToString(CultureInfo.InvariantCulture));
        }

        arguments.AddRange(
        [
            "--output", reportPath,
            "--quality-boundary", Path.GetFullPath(request.QualityBoundaryPath, workingDirectory),
            "--worker-performance-file", performancePath,
            "--summary-output", summaryPath,
            "--scenario-name", scenario.Name
        ]);
        if (!string.IsNullOrWhiteSpace(request.SourceCommit))
        {
            arguments.AddRange(["--source-commit", request.SourceCommit]);
        }

        if (request.SourceTreeDirty is { } dirty)
        {
            arguments.AddRange(["--source-tree-dirty", dirty ? "true" : "false"]);
        }

        StressNetworkProxyPlan? proxy = null;
        if (networkProfilePath is not null)
        {
            arguments.AddRange(["--network-profile", networkProfilePath]);
            var countsPath = Path.Combine(runFolder, StressNetworkProfiles.CountsFileName);
            proxy = new StressNetworkProxyPlan(
                advertisedPort,
                udpPort,
                [
                    "--profile", networkProfilePath,
                    "--listen", "127.0.0.1:" + advertisedPort.ToString(CultureInfo.InvariantCulture),
                    "--forward", "127.0.0.1:" + udpPort.ToString(CultureInfo.InvariantCulture),
                    "--seed", generator.GetValueOrDefault("seed", StressNetworkProxyPlan.DefaultSeed).ToString(CultureInfo.InvariantCulture),
                    "--counts", countsPath,
                    "--report-interval-seconds", "10"
                ],
                countsPath);
        }

        // The generator validates its arguments exactly as it will when the run starts; the World
        // variant does not exist yet, so its path is checked when the run starts.
        StressGeneratorOptions.Parse(
            arguments.ToArray(),
            workingDirectory,
            name => generatorEnvironment.GetValueOrDefault(name));
        StressQualityBoundary.Load(Path.GetFullPath(request.QualityBoundaryPath, workingDirectory));

        return new StressLoadPlan(
            scenario.Name,
            scenario.Description,
            StressGeneratorOptions.FormatMode(scenario.Mode),
            StressGeneratorOptions.FormatWorkload(scenario.Workload),
            fullStack,
            fullStack,
            fullStack ? FullStackGameServiceUrl : null,
            fullStack ? FullStackGameServiceUrl : WorkerOnlyAuthorityUrl,
            scenario.ShardId,
            udpPort,
            fullStack ? [FullStackGameServicePort] : [WorkerOnlyAuthorityPort],
            fullStack ? [PostgresPort, RedisPort] : [],
            proxy is null ? [udpPort] : [udpPort, proxy.UdpPort],
            gameServiceEnvironment,
            workerEnvironment,
            generatorEnvironment,
            arguments,
            SensitiveNames,
            reportPath,
            summaryPath,
            performancePath)
        {
            WorldVariantPath = worldVariantPath,
            Network = networkProfile?.Name ?? NetworkProfile.None,
            NetworkProfilePath = networkProfilePath,
            Proxy = proxy,
            Outage = outagePlan
        };
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                DictionaryKeyPolicy = null
            });
    }

    // GameService's database pool, the time after which it takes a silent worker offline, and its
    // retention window of item history and retention interval: its checked-in configuration,
    // unless the scenario sets them for GameService.
    private static (int PoolSize, int HeartbeatTimeoutSeconds, int ItemHistorySeconds, int RetentionIntervalSeconds) GameServiceLimits(
        StressLoadScenario scenario,
        string workingDirectory)
    {
        var path = Path.Combine(
            Mmorpg.WorldData.Content.ContentLayout.FindRepositoryRoot(workingDirectory),
            "src",
            "Server",
            "GameService",
            "Config",
            "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var pool = Setting(scenario, "Database__MaximumPoolSize")
            ?? root.GetProperty("Database").GetProperty("MaximumPoolSize").GetInt32();
        var timeout = Setting(scenario, "Simulation__WorkerHeartbeatTimeoutSeconds")
            ?? root.GetProperty("Simulation").GetProperty("WorkerHeartbeatTimeoutSeconds").GetInt32();
        var itemHistory = scenario.GameServiceSettings.GetValueOrDefault("Retention__ItemHistory")
            ?? root.GetProperty("Retention").GetProperty("ItemHistory").GetString()!;
        if (!TimeSpan.TryParse(itemHistory, CultureInfo.InvariantCulture, out var window) || window <= TimeSpan.Zero)
        {
            throw new StressGeneratorOptionException(
                $"The scenario '{scenario.Name}' gives GameService the retention window '{itemHistory}', which is no time span such as 00:04:00.");
        }

        var interval = Setting(scenario, "Retention__IntervalSeconds")
            ?? root.GetProperty("Retention").GetProperty("IntervalSeconds").GetInt32();
        return (pool, timeout, (int)Math.Ceiling(window.TotalSeconds), interval);
    }

    private static int? Setting(StressLoadScenario scenario, string name)
    {
        return scenario.GameServiceSettings.TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }

    // A scenario adds settings; it never replaces one that the plan owns, except the metrics
    // interval.
    // A steady interval too short for the worker's reporting interval could never be judged.
    private static void RequireSteadyWorkerIntervals(string scenario, int steadySeconds, string metricsInterval)
    {
        if (!int.TryParse(metricsInterval, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 1)
        {
            throw new StressGeneratorOptionException(
                $"The scenario '{scenario}' sets '{MetricsIntervalSetting}' to '{metricsInterval}', which is not a whole number of seconds.");
        }

        if (steadySeconds < seconds * (MinimumSteadyWorkerIntervals + 1))
        {
            throw new StressGeneratorOptionException(
                $"The scenario '{scenario}' has a steady interval of {steadySeconds} s, which does not hold "
                + $"{MinimumSteadyWorkerIntervals} whole worker intervals of {seconds} s beside a partial one at each end. "
                + "Lengthen the run or shorten the worker interval.");
        }
    }

    private static void Merge(
        Dictionary<string, string> environment,
        IReadOnlyDictionary<string, string> settings,
        string section,
        string scenario)
    {
        foreach (var setting in settings)
        {
            if (environment.ContainsKey(setting.Key) && setting.Key != MetricsIntervalSetting)
            {
                throw new StressGeneratorOptionException(
                    $"The scenario '{scenario}' cannot set '{setting.Key}' in '{section}', because the plan sets it.");
            }

            environment[setting.Key] = setting.Value;
        }
    }
}

// The outage section of a scenario: how far into the steady interval GameService stops, and how
// long it stays stopped.
public sealed record StressOutageScenario(int AtSeconds, int Seconds)
{
    public static StressOutageScenario Parse(JsonElement element, string source)
    {
        StrictJson.RequireObject(element, source, "the outage");
        StrictJson.RequireOnly(element, source, "the outage", "atSeconds", "seconds");
        return new StressOutageScenario(
            RequireSeconds(element, "atSeconds", source),
            RequireSeconds(element, "seconds", source));
    }

    private static int RequireSeconds(JsonElement element, string name, string source)
    {
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var seconds)
            && seconds > 0
                ? seconds
                : throw new StressGeneratorOptionException($"'{source}': the outage needs a positive whole number '{name}'.");
    }
}

// What tools/Test-Load.ps1 does for a scenario's outage: AtSeconds into the steady interval the
// generator writes RequestPath; the script then stops GameService, starts it again Seconds after
// the stop with the same settings, and writes the times to WindowPath.
public sealed record StressOutagePlan(int AtSeconds, int Seconds, string RequestPath, string WindowPath);

// The network impairment proxy of a run under a network profile: it listens on UdpPort, which
// the worker advertises, forwards to the worker's own ForwardUdpPort, and writes its counts to
// CountsPath when it stops.
public sealed record StressNetworkProxyPlan(
    int UdpPort,
    int ForwardUdpPort,
    IReadOnlyList<string> Arguments,
    string CountsPath)
{
    // The generator's seed when the scenario names none, which the proxy then uses too.
    public const int DefaultSeed = 1337;
}

public sealed record StressLoadPlanRequest(
    string RunFolder,
    string QualityBoundaryPath,
    int? Bots = null,
    int? DurationSeconds = null,
    string? SourceCommit = null,
    bool? SourceTreeDirty = null,
    string? Network = null)
{
    public const string DefaultQualityBoundaryPath = "tools/load/quality-boundary.json";

    // StackStressGenerator plan --scenario <file> --run-folder <dir> [--bots <n>]
    // [--duration-seconds <n>] [--quality-boundary <file>] [--source-commit <sha>]
    // [--source-tree-dirty true|false] [--network <profile|none>]
    public static (string ScenarioPath, StressLoadPlanRequest Request) Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                throw new StressGeneratorOptionException($"The plan argument '{key}' needs a value.");
            }

            if (key is not ("--scenario" or "--run-folder" or "--bots" or "--duration-seconds"
                or "--quality-boundary" or "--source-commit" or "--source-tree-dirty" or "--network"))
            {
                throw new StressGeneratorOptionException($"The plan argument '{key}' is unknown.");
            }

            if (!values.TryAdd(key, args[index + 1]))
            {
                throw new StressGeneratorOptionException($"The plan argument '{key}' was given twice.");
            }
        }

        if (!values.TryGetValue("--scenario", out var scenario) || !values.TryGetValue("--run-folder", out var runFolder))
        {
            throw new StressGeneratorOptionException("The plan needs --scenario and --run-folder.");
        }

        return (scenario, new StressLoadPlanRequest(
            runFolder,
            values.GetValueOrDefault("--quality-boundary", DefaultQualityBoundaryPath),
            OptionalInt(values, "--bots"),
            OptionalInt(values, "--duration-seconds"),
            values.GetValueOrDefault("--source-commit"),
            values.TryGetValue("--source-tree-dirty", out var dirty)
                ? bool.TryParse(dirty, out var parsed)
                    ? parsed
                    : throw new StressGeneratorOptionException("--source-tree-dirty must be true or false.")
                : null,
            values.GetValueOrDefault("--network")));
    }

    private static int? OptionalInt(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new StressGeneratorOptionException($"The plan argument '{key}' must be a positive integer.");
    }
}
