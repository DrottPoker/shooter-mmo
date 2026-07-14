using System.Globalization;

namespace ShooterMmo.Tools.ActiveSimulationBots;

public sealed record ActiveSimulationBotOptions(
    Uri AuthorityUrl,
    string AuthoritySecret,
    string ShardId,
    int MinimumActiveBots,
    int MaximumActiveBots,
    double StartupBotsPerSecond,
    TimeSpan PopulationChangeInterval,
    TimeSpan MinimumOnlineDuration,
    TimeSpan MaximumOnlineDuration,
    TimeSpan MinimumOfflineDuration,
    TimeSpan MaximumOfflineDuration,
    TimeSpan MinimumDirectionDuration,
    TimeSpan MaximumDirectionDuration,
    double IdleChance,
    double SprintChance,
    double AimChance,
    double JumpChancePerSecond,
    TimeSpan JoinAndLeaveTimeout,
    TimeSpan MinimumRetryDelay,
    TimeSpan MaximumRetryDelay,
    TimeSpan ShutdownTimeout,
    TimeSpan StatusInterval,
    int Seed)
{
    public const string SectionName = "ActiveSimulationBots";
    public const string SharedSecretEnvironmentVariable = "ACTIVE_SIMULATION_BOTS_SECRET";
    public const int AbsoluteMaximumBots = 10_000;

    public static ActiveSimulationBotOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetRequiredSection(SectionName);
        var errors = new List<string>();

        var authorityUrlText = section["AuthorityUrl"];
        if (!Uri.TryCreate(authorityUrlText, UriKind.Absolute, out var authorityUrl)
            || authorityUrl.Scheme != Uri.UriSchemeHttp
            || !authorityUrl.IsLoopback)
        {
            errors.Add($"{SectionName}:AuthorityUrl must be an absolute loopback HTTP URL.");
            authorityUrl = new Uri("http://127.0.0.1:5000");
        }

        var authoritySecret = section["AuthoritySecret"];
        if (string.IsNullOrWhiteSpace(authoritySecret))
        {
            authoritySecret = configuration[SharedSecretEnvironmentVariable];
        }

        if (string.IsNullOrWhiteSpace(authoritySecret)
            || authoritySecret.Length < 32
            || authoritySecret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{SectionName}:AuthoritySecret or {SharedSecretEnvironmentVariable} must contain a non-placeholder secret of at least 32 characters.");
        }

        var shardId = section["ShardId"]?.Trim() ?? string.Empty;
        if (!IsValidIdentifier(shardId))
        {
            errors.Add($"{SectionName}:ShardId must be a valid identifier.");
        }

        var minimumActiveBots = ReadInt(section, "MinimumActiveBots", 1, 0, AbsoluteMaximumBots, errors);
        var maximumActiveBots = ReadInt(section, "MaximumActiveBots", 1, 1, AbsoluteMaximumBots, errors);
        if (minimumActiveBots > maximumActiveBots)
        {
            errors.Add($"{SectionName}:MinimumActiveBots cannot exceed MaximumActiveBots.");
        }

        var startupBotsPerSecond = ReadDouble(
            section,
            "StartupBotsPerSecond",
            1d,
            0.1d,
            1000d,
            errors);
        var populationChangeInterval = ReadSeconds(
            section,
            "PopulationChangeIntervalSeconds",
            20,
            1,
            3600,
            errors);
        var minimumOnlineDuration = ReadSeconds(
            section,
            "MinimumOnlineSeconds",
            45,
            1,
            86_400,
            errors);
        var maximumOnlineDuration = ReadSeconds(
            section,
            "MaximumOnlineSeconds",
            120,
            1,
            86_400,
            errors);
        var minimumOfflineDuration = ReadSeconds(
            section,
            "MinimumOfflineSeconds",
            5,
            0,
            86_400,
            errors);
        var maximumOfflineDuration = ReadSeconds(
            section,
            "MaximumOfflineSeconds",
            25,
            0,
            86_400,
            errors);
        var minimumDirectionDuration = ReadSeconds(
            section,
            "MinimumDirectionSeconds",
            2,
            1,
            3600,
            errors);
        var maximumDirectionDuration = ReadSeconds(
            section,
            "MaximumDirectionSeconds",
            6,
            1,
            3600,
            errors);
        ValidateRange(
            minimumOnlineDuration,
            maximumOnlineDuration,
            "MinimumOnlineSeconds",
            "MaximumOnlineSeconds",
            errors);
        ValidateRange(
            minimumOfflineDuration,
            maximumOfflineDuration,
            "MinimumOfflineSeconds",
            "MaximumOfflineSeconds",
            errors);
        ValidateRange(
            minimumDirectionDuration,
            maximumDirectionDuration,
            "MinimumDirectionSeconds",
            "MaximumDirectionSeconds",
            errors);

        var idleChance = ReadProbability(section, "IdleChance", 0.15d, errors);
        var sprintChance = ReadProbability(section, "SprintChance", 0.45d, errors);
        var aimChance = ReadProbability(section, "AimChance", 0.20d, errors);
        var jumpChancePerSecond = ReadProbability(
            section,
            "JumpChancePerSecond",
            0.08d,
            errors);

        var joinAndLeaveTimeout = ReadSeconds(
            section,
            "JoinAndLeaveTimeoutSeconds",
            10,
            1,
            300,
            errors);
        var minimumRetryDelay = ReadSeconds(
            section,
            "MinimumRetrySeconds",
            2,
            1,
            3600,
            errors);
        var maximumRetryDelay = ReadSeconds(
            section,
            "MaximumRetrySeconds",
            30,
            1,
            3600,
            errors);
        ValidateRange(
            minimumRetryDelay,
            maximumRetryDelay,
            "MinimumRetrySeconds",
            "MaximumRetrySeconds",
            errors);

        var shutdownTimeout = ReadSeconds(
            section,
            "ShutdownTimeoutSeconds",
            15,
            1,
            300,
            errors);
        var statusInterval = ReadSeconds(
            section,
            "StatusIntervalSeconds",
            5,
            1,
            3600,
            errors);
        var seed = ReadInt(
            section,
            "Seed",
            1337,
            int.MinValue,
            int.MaxValue,
            errors);

        if (errors.Count > 0)
        {
            throw new ActiveSimulationBotConfigurationException(
                "ActiveSimulationBots configuration is invalid:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new ActiveSimulationBotOptions(
            authorityUrl,
            authoritySecret!,
            shardId,
            minimumActiveBots,
            maximumActiveBots,
            startupBotsPerSecond,
            populationChangeInterval,
            minimumOnlineDuration,
            maximumOnlineDuration,
            minimumOfflineDuration,
            maximumOfflineDuration,
            minimumDirectionDuration,
            maximumDirectionDuration,
            idleChance,
            sprintChance,
            aimChance,
            jumpChancePerSecond,
            joinAndLeaveTimeout,
            minimumRetryDelay,
            maximumRetryDelay,
            shutdownTimeout,
            statusInterval,
            seed);
    }

    private static int ReadInt(
        IConfiguration section,
        string key,
        int fallback,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        var text = section[key];
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            errors.Add($"{SectionName}:{key} must be between {minimum} and {maximum}.");
            return fallback;
        }

        return value;
    }

    private static double ReadDouble(
        IConfiguration section,
        string key,
        double fallback,
        double minimum,
        double maximum,
        ICollection<string> errors)
    {
        var text = section[key];
        if (text is null)
        {
            return fallback;
        }

        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
        {
            errors.Add($"{SectionName}:{key} must be between {minimum} and {maximum}.");
            return fallback;
        }

        return value;
    }

    private static double ReadProbability(
        IConfiguration section,
        string key,
        double fallback,
        ICollection<string> errors)
    {
        return ReadDouble(section, key, fallback, 0d, 1d, errors);
    }

    private static TimeSpan ReadSeconds(
        IConfiguration section,
        string key,
        int fallback,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        return TimeSpan.FromSeconds(ReadInt(section, key, fallback, minimum, maximum, errors));
    }

    private static void ValidateRange(
        TimeSpan minimum,
        TimeSpan maximum,
        string minimumKey,
        string maximumKey,
        ICollection<string> errors)
    {
        if (minimum > maximum)
        {
            errors.Add($"{SectionName}:{minimumKey} cannot exceed {maximumKey}.");
        }
    }

    private static bool IsValidIdentifier(string value)
    {
        return value.Length is > 0 and <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}

public sealed class ActiveSimulationBotConfigurationException(string message) :
    Exception(message);
