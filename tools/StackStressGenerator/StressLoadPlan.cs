using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        "poll-threads"
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
                "worldVariant");
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
                WorldVariant = worldVariant
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

        var gameServiceEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var workerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var generatorEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = new List<string>();
        var udpPort = fullStack ? FullStackWorkerUdpPort : WorkerOnlyUdpPort;
        if (fullStack)
        {
            gameServiceEnvironment["ConnectionStrings__Postgres"] = PostgresConnection;
            gameServiceEnvironment["ConnectionStrings__Redis"] = RedisConnection;
            gameServiceEnvironment["SIMULATION_WORKER_SERVICE_SECRET"] = FullStackWorkerSecret;
            gameServiceEnvironment["DevelopmentSimulationBots__Enabled"] = "false";
            // Covers the registration and login of every bot within one minute.
            gameServiceEnvironment["RateLimiting__Authentication__PermitLimit"] = "1000";
            gameServiceEnvironment["RateLimiting__Authentication__WindowSeconds"] = "60";
            gameServiceEnvironment["ASPNETCORE_URLS"] = FullStackGameServiceUrl;
            if (gameplay)
            {
                gameServiceEnvironment["DOTNET_ENVIRONMENT"] = "Development";
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

            // The soak reads Redis and compares the drift checks with GameService's own limits.
            var (poolSize, heartbeatTimeout) = GameServiceLimits(scenario, workingDirectory);
            arguments.AddRange(
            [
                "--redis-endpoint", RedisConnection,
                "--database-pool-size", poolSize.ToString(CultureInfo.InvariantCulture),
                "--worker-heartbeat-timeout-seconds", heartbeatTimeout.ToString(CultureInfo.InvariantCulture)
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
                "--worker-udp-port", udpPort.ToString(CultureInfo.InvariantCulture),
                "--fleet-id", "local-fleet",
                "--node-id", "local-node-1",
                "--worker-secret", workerSecret,
                "--authority-hold-seconds", "120"
            ]);
        }

        workerEnvironment["SIMULATION_WORKER_UDP_PORT"] = udpPort.ToString(CultureInfo.InvariantCulture);
        workerEnvironment["SIMULATION_WORKER_ADVERTISED_HOST"] = "127.0.0.1";
        workerEnvironment["SIMULATION_WORKER_ADVERTISED_UDP_PORT"] = udpPort.ToString(CultureInfo.InvariantCulture);
        workerEnvironment[MetricsIntervalSetting] =
            MetricsIntervalSeconds(generator["duration-seconds"]).ToString(CultureInfo.InvariantCulture);
        workerEnvironment["SimulationWorker__PerformanceReportPath"] = performancePath;

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
            [udpPort],
            gameServiceEnvironment,
            workerEnvironment,
            generatorEnvironment,
            arguments,
            SensitiveNames,
            reportPath,
            summaryPath,
            performancePath)
        {
            WorldVariantPath = worldVariantPath
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

    // GameService's database pool and the time after which it takes a silent worker offline:
    // its checked-in configuration, unless the scenario sets them for GameService.
    private static (int PoolSize, int HeartbeatTimeoutSeconds) GameServiceLimits(
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
        return (pool, timeout);
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

public sealed record StressLoadPlanRequest(
    string RunFolder,
    string QualityBoundaryPath,
    int? Bots = null,
    int? DurationSeconds = null,
    string? SourceCommit = null,
    bool? SourceTreeDirty = null)
{
    public const string DefaultQualityBoundaryPath = "tools/load/quality-boundary.json";

    // StackStressGenerator plan --scenario <file> --run-folder <dir> [--bots <n>]
    // [--duration-seconds <n>] [--quality-boundary <file>] [--source-commit <sha>]
    // [--source-tree-dirty true|false]
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
                or "--quality-boundary" or "--source-commit" or "--source-tree-dirty"))
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
                : null));
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
