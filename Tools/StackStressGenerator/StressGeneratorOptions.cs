using System.Globalization;
using System.Security.Cryptography;

namespace ShooterMmo.Tools.StackStressGenerator;

public enum StressRunMode
{
    WorkerOnly,
    FullStack
}

public enum StressWorkloadProfile
{
    Lifecycle,
    Inventory,
    LootHotspot,
    MixedGameplay
}

public sealed record StressGeneratorOptions(
    StressRunMode Mode,
    StressWorkloadProfile Workload,
    Uri AuthorityUrl,
    string WorkerHost,
    int WorkerUdpPort,
    string WorkerId,
    string FleetId,
    string NodeId,
    string ShardId,
    string WorldId,
    string? WorkerSecret,
    string? PostgresConnectionString,
    string? ConfirmedDisposableDatabase,
    string? FixtureSecret,
    string RunId,
    TimeSpan HttpTimeout,
    int HttpConcurrency,
    int BotCount,
    int BotStartIndex,
    int RampStep,
    TimeSpan RampInterval,
    TimeSpan SteadyDuration,
    TimeSpan WorkerWaitTimeout,
    TimeSpan JoinTimeout,
    TimeSpan InventoryOperationInterval,
    TimeSpan LootOperationInterval,
    TimeSpan GameplayOperationTimeout,
    int LootHotspotX,
    int LootHotspotY,
    int LootHotspotZ,
    TimeSpan ReportInterval,
    int Seed,
    string OutputPath)
{
    public const int MaximumBotCount = 10_000;
    public const string PostgresConnectionEnvironmentVariable = "STACK_STRESS_POSTGRES";
    public const string FixtureSecretEnvironmentVariable = "STACK_STRESS_FIXTURE_SECRET";

    public static StressGeneratorOptions Parse(string[] args, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var values = ParseValues(args);
        var mode = ParseMode(Get(values, "mode", "worker-only"));
        var workload = ParseWorkload(Get(values, "workload", "lifecycle"));
        if (mode == StressRunMode.WorkerOnly
            && workload != StressWorkloadProfile.Lifecycle)
        {
            throw new StressGeneratorOptionException(
                "Inventory and loot workload profiles require --mode full-stack because synthetic worker-only sessions cannot mutate durable items.");
        }
        var authorityKey = mode == StressRunMode.WorkerOnly
            ? "authority-url"
            : "auth-service-url";
        var authorityUrl = ParseHttpUri(
            Get(
                values,
                authorityKey,
                mode == StressRunMode.WorkerOnly
                    ? "http://127.0.0.1:5099"
                    : "http://127.0.0.1:5000"),
            authorityKey);
        if (!authorityUrl.IsLoopback)
        {
            throw new StressGeneratorOptionException(
                $"--{authorityKey} must use a loopback address so stress traffic is not sent to a remote environment.");
        }

        string? workerSecret = null;
        string? postgresConnectionString = null;
        string? confirmedDisposableDatabase = null;
        string? fixtureSecret = null;
        if (mode == StressRunMode.WorkerOnly)
        {
            workerSecret = Get(values, "worker-secret", CreateSecret());
            if (workerSecret.Length < 32)
            {
                throw new StressGeneratorOptionException(
                    "--worker-secret must contain at least 32 characters.");
            }
        }
        else
        {
            postgresConnectionString = GetOptional(values, "postgres-connection-string")
                ?? Environment.GetEnvironmentVariable(PostgresConnectionEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(postgresConnectionString))
            {
                throw new StressGeneratorOptionException(
                    $"Full-stack mode requires --postgres-connection-string or {PostgresConnectionEnvironmentVariable}.");
            }

            confirmedDisposableDatabase = GetOptional(values, "confirm-disposable-database");
            if (string.IsNullOrWhiteSpace(confirmedDisposableDatabase))
            {
                throw new StressGeneratorOptionException(
                    "Full-stack mode requires --confirm-disposable-database with the exact stress database name.");
            }

            if (workload != StressWorkloadProfile.Lifecycle)
            {
                fixtureSecret = Environment.GetEnvironmentVariable(
                    FixtureSecretEnvironmentVariable)?.Trim();
                if (string.IsNullOrWhiteSpace(fixtureSecret)
                    || fixtureSecret.Length < 32)
                {
                    throw new StressGeneratorOptionException(
                        $"The {FormatWorkload(workload)} workload requires {FixtureSecretEnvironmentVariable} with at least 32 characters.");
                }
            }
        }

        var botCount = ParseInt(values, "bots", 100, 1, MaximumBotCount);
        var botStartIndex = ParseInt(values, "bot-start-index", 1, 1, int.MaxValue - botCount);
        var rampStep = ParseInt(values, "ramp-step", Math.Min(25, botCount), 1, botCount);
        var output = Get(
            values,
            "output",
            Path.Combine(
                workingDirectory,
                "artifacts",
                "stress",
                $"stack-stress-{FormatMode(mode)}-{FormatWorkload(workload)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"));
        var runId = ValidateRunId(Get(values, "run-id", CreateRunId()));

        return new StressGeneratorOptions(
            mode,
            workload,
            authorityUrl,
            Require(Get(values, "worker-host", "127.0.0.1"), "worker-host"),
            ParseInt(values, "worker-udp-port", 27015, 1, ushort.MaxValue),
            Require(Get(values, "worker-id", "local-simulation-worker-1"), "worker-id"),
            Require(Get(values, "fleet-id", "local-fleet"), "fleet-id"),
            Require(Get(values, "node-id", "local-node-1"), "node-id"),
            Require(Get(values, "shard-id", "local-shard-1"), "shard-id"),
            Require(Get(values, "world-id", "development-world-2"), "world-id"),
            workerSecret,
            postgresConnectionString?.Trim(),
            confirmedDisposableDatabase?.Trim(),
            fixtureSecret,
            runId,
            TimeSpan.FromSeconds(ParseInt(values, "http-timeout-seconds", 30, 1, 300)),
            ParseInt(values, "http-concurrency", 32, 1, 512),
            botCount,
            botStartIndex,
            rampStep,
            TimeSpan.FromSeconds(ParseInt(values, "ramp-interval-seconds", 10, 1, 3600)),
            TimeSpan.FromSeconds(ParseInt(values, "duration-seconds", 300, 1, 86_400)),
            TimeSpan.FromSeconds(ParseInt(values, "worker-wait-seconds", 120, 1, 3600)),
            TimeSpan.FromSeconds(ParseInt(values, "join-timeout-seconds", 10, 1, 300)),
            TimeSpan.FromSeconds(ParseInt(
                values,
                "inventory-operation-interval-seconds",
                8,
                1,
                3600)),
            TimeSpan.FromSeconds(ParseInt(
                values,
                "loot-operation-interval-seconds",
                3,
                1,
                3600)),
            TimeSpan.FromSeconds(ParseInt(
                values,
                "gameplay-operation-timeout-seconds",
                30,
                1,
                300)),
            ParseInt(values, "loot-hotspot-x", 0, -1_000_000, 1_000_000),
            ParseInt(values, "loot-hotspot-y", 0, -1_000_000, 1_000_000),
            ParseInt(values, "loot-hotspot-z", -16, -1_000_000, 1_000_000),
            TimeSpan.FromSeconds(ParseInt(values, "report-interval-seconds", 10, 1, 3600)),
            ParseInt(values, "seed", 1337, int.MinValue, int.MaxValue),
            Path.GetFullPath(output, workingDirectory));
    }

    public static bool IsHelpRequested(string[] args)
    {
        return args.Any(argument => argument is "--help" or "-h" or "/?");
    }

    public static string HelpText =>
        """
        StackStressGenerator

        Drives headless LiteNetLib bots through worker-only or full-stack account,
        placement, session, join, movement, item, loot, and leave flows.

        Options:
          --mode <worker-only|full-stack>   Run mode. Default: worker-only
          --workload <profile>              lifecycle, inventory, loot-hotspot, or mixed-gameplay. Default: lifecycle
          --authority-url <url>             Loopback authority URL. Default: http://127.0.0.1:5099
          --auth-service-url <url>          Full-stack AuthService URL. Default: http://127.0.0.1:5000
          --postgres-connection-string <cs> Full-stack metrics and safety connection. Prefer STACK_STRESS_POSTGRES.
          --confirm-disposable-database <n> Exact disposable database name required by full-stack mode.
          --run-id <id>                     Short identifier used in durable stress identities.
          --http-timeout-seconds <seconds>  Full-stack HTTP timeout. Default: 30
          --http-concurrency <count>        Maximum concurrent full-stack lifecycles. Default: 32
          --worker-host <host>              UDP host used by bots. Default: 127.0.0.1
          --worker-udp-port <port>          UDP port used by bots. Default: 27015
          --worker-id <id>                  Expected worker id. Default: local-simulation-worker-1
          --fleet-id <id>                   Expected fleet id. Default: local-fleet
          --node-id <id>                    Expected node id. Default: local-node-1
          --shard-id <id>                   Expected shard id. Default: local-shard-1
          --world-id <id>                   Expected World. Default: development-world-2
          --worker-secret <secret>          Ephemeral worker secret. A random value is generated by default.
          --bots <count>                    Total bots. Default: 100
          --bot-start-index <index>         First bot display index. Default: 1
          --ramp-step <count>               Bots added per ramp interval. Default: 25
          --ramp-interval-seconds <seconds> Delay between ramp steps. Default: 10
          --duration-seconds <seconds>      Steady-load duration after the ramp. Default: 300
          --worker-wait-seconds <seconds>   Worker registration timeout. Default: 120
          --join-timeout-seconds <seconds>  Per-bot join timeout. Default: 10
          --inventory-operation-interval-seconds <seconds> Inventory mutation cadence. Default: 8
          --loot-operation-interval-seconds <seconds> Corpse mutation cadence. Default: 3
          --gameplay-operation-timeout-seconds <seconds> Realtime operation timeout. Default: 30
          --loot-hotspot-x|y|z <coordinate> Corpse position. Default: 0, 0, -16
          --report-interval-seconds <sec>   Console report interval. Default: 10
          --seed <number>                   Deterministic movement seed. Default: 1337
          --output <path>                   JSON result path under artifacts/stress by default.
        """;

    private static Dictionary<string, string> ParseValues(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h" or "/?")
            {
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal)
                || argument.Length <= 2)
            {
                throw new StressGeneratorOptionException(
                    $"Unknown argument '{argument}'. Use --help to list supported options.");
            }

            var key = argument[2..];
            if (index + 1 >= args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new StressGeneratorOptionException($"Option '--{key}' requires a value.");
            }

            if (!values.TryAdd(key, args[++index]))
            {
                throw new StressGeneratorOptionException($"Option '--{key}' was provided more than once.");
            }
        }

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mode",
            "workload",
            "authority-url",
            "auth-service-url",
            "postgres-connection-string",
            "confirm-disposable-database",
            "run-id",
            "http-timeout-seconds",
            "http-concurrency",
            "worker-host",
            "worker-udp-port",
            "worker-id",
            "fleet-id",
            "node-id",
            "shard-id",
            "world-id",
            "worker-secret",
            "bots",
            "bot-start-index",
            "ramp-step",
            "ramp-interval-seconds",
            "duration-seconds",
            "worker-wait-seconds",
            "join-timeout-seconds",
            "inventory-operation-interval-seconds",
            "loot-operation-interval-seconds",
            "gameplay-operation-timeout-seconds",
            "loot-hotspot-x",
            "loot-hotspot-y",
            "loot-hotspot-z",
            "report-interval-seconds",
            "seed",
            "output"
        };
        var unsupported = values.Keys.FirstOrDefault(key => !supported.Contains(key));
        if (unsupported is not null)
        {
            throw new StressGeneratorOptionException(
                $"Unknown option '--{unsupported}'. Use --help to list supported options.");
        }

        return values;
    }

    private static StressRunMode ParseMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "worker-only" => StressRunMode.WorkerOnly,
            "full-stack" => StressRunMode.FullStack,
            _ => throw new StressGeneratorOptionException(
                "--mode must be either worker-only or full-stack.")
        };
    }

    private static StressWorkloadProfile ParseWorkload(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "lifecycle" => StressWorkloadProfile.Lifecycle,
            "inventory" => StressWorkloadProfile.Inventory,
            "loot-hotspot" => StressWorkloadProfile.LootHotspot,
            "mixed-gameplay" => StressWorkloadProfile.MixedGameplay,
            _ => throw new StressGeneratorOptionException(
                "--workload must be lifecycle, inventory, loot-hotspot, or mixed-gameplay.")
        };
    }

    public static string FormatMode(StressRunMode mode)
    {
        return mode switch
        {
            StressRunMode.WorkerOnly => "worker-only",
            StressRunMode.FullStack => "full-stack",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static string FormatWorkload(StressWorkloadProfile workload)
    {
        return workload switch
        {
            StressWorkloadProfile.Lifecycle => "lifecycle",
            StressWorkloadProfile.Inventory => "inventory",
            StressWorkloadProfile.LootHotspot => "loot-hotspot",
            StressWorkloadProfile.MixedGameplay => "mixed-gameplay",
            _ => throw new ArgumentOutOfRangeException(nameof(workload))
        };
    }

    private static string ValidateRunId(string value)
    {
        var runId = Require(value, "run-id");
        if (runId.Length > 12
            || !runId.All(character => char.IsAsciiLetterOrDigit(character)))
        {
            throw new StressGeneratorOptionException(
                "--run-id must contain 1 to 12 ASCII letters or numbers.");
        }

        return runId.ToLowerInvariant();
    }

    private static string Get(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback)
    {
        return values.TryGetValue(key, out var value) ? value : fallback;
    }

    private static string? GetOptional(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static int ParseInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new StressGeneratorOptionException(
                $"--{key} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    private static Uri ParseHttpUri(string value, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new StressGeneratorOptionException(
                $"--{key} must be an absolute HTTP URL.");
        }

        return uri;
    }

    private static string Require(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new StressGeneratorOptionException($"--{key} cannot be empty.");
        }

        return value.Trim();
    }

    private static string CreateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string CreateRunId()
    {
        return $"{DateTime.UtcNow:MMddHHmm}{RandomNumberGenerator.GetInt32(0, 10_000):D4}";
    }
}

public sealed class StressGeneratorOptionException(string message) : Exception(message);
