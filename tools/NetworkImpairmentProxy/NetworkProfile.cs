using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mmorpg.Tools.NetworkImpairmentProxy;

// A network profile of tools/load/network-profiles: what the proxy does to each direction of
// every client's traffic, and what the load runner's verdict allows under it. The file is read
// strictly: an unknown property, a missing value, or a value out of range is an error.
public sealed record NetworkProfile(
    string Name,
    string Description,
    DirectionProfile ClientToServer,
    DirectionProfile ServerToClient,
    NetworkProfileVerdict Verdict)
{
    // The name that means no proxy at all; no profile file may take it.
    public const string None = "none";

    public const string FolderName = "network-profiles";

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    // The round trip that the delays add, and how far the jitter moves it at most.
    public double RoundTripMs => ClientToServer.DelayMs + ServerToClient.DelayMs;

    public double RoundTripJitterMs => ClientToServer.JitterMs + ServerToClient.JitterMs;

    public bool HasOutages => ClientToServer.Outages is not null || ServerToClient.Outages is not null;

    public static bool IsValidName(string name)
    {
        return NamePattern.IsMatch(name) && name != None;
    }

    // The folder below the proxy's output, where the build copies the checked-in profiles.
    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "NetworkProfiles");

    // A name is looked up in the bundled profiles; anything with a path separator or a .json
    // ending is a file.
    public static NetworkProfile Resolve(string nameOrPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        if (nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || nameOrPath.Contains('/', StringComparison.Ordinal)
            || nameOrPath.Contains('\\', StringComparison.Ordinal))
        {
            return Load(Path.GetFullPath(nameOrPath, workingDirectory));
        }

        if (nameOrPath == None)
        {
            throw new NetworkImpairmentException(
                "The profile 'none' means no proxy; run the client against the worker directly.");
        }

        if (!IsValidName(nameOrPath))
        {
            throw new NetworkImpairmentException(
                $"'{nameOrPath}' is not a profile name: lowercase letters, digits, and dashes, starting with a letter.");
        }

        var path = Path.Combine(BundledFolder, nameOrPath + ".json");
        if (!File.Exists(path))
        {
            var known = Directory.Exists(BundledFolder)
                ? string.Join(", ", Directory.EnumerateFiles(BundledFolder, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Order(StringComparer.Ordinal))
                : "none bundled";
            throw new NetworkImpairmentException($"The profile '{nameOrPath}' does not exist. Profiles: {known}.");
        }

        return Load(path);
    }

    public static NetworkProfile Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new NetworkImpairmentException($"The profile '{path}' cannot be read: {exception.Message}");
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (!IsValidName(name))
        {
            throw new NetworkImpairmentException(
                $"The profile file '{path}' has no valid profile name: lowercase letters, digits, and dashes, starting with a letter, and not '{None}'.");
        }

        return Parse(name, text, path);
    }

    public static NetworkProfile Parse(string name, string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new NetworkImpairmentException($"'{source}' is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var reader = new StrictReader(source);
            reader.RequireObject(root, "the profile");
            reader.RequireOnly(root, "the profile", "description", "clientToServer", "serverToClient", "verdict");
            return new NetworkProfile(
                name,
                reader.RequireString(root, "description", "the profile"),
                ReadDirection(reader, root, "clientToServer"),
                ReadDirection(reader, root, "serverToClient"),
                ReadVerdict(reader, root));
        }
    }

    private static DirectionProfile ReadDirection(StrictReader reader, JsonElement root, string name)
    {
        var element = reader.RequireProperty(root, name, "the profile");
        reader.RequireObject(element, $"'{name}'");
        reader.RequireOnly(element, $"'{name}'", "delayMs", "jitterMs", "lossPercent", "duplicatePercent", "burstLoss", "outages");
        var delay = reader.RequireNumber(element, "delayMs", name, 0, 10_000);
        var jitter = reader.RequireNumber(element, "jitterMs", name, 0, 10_000);
        if (jitter > delay)
        {
            throw reader.Invalid($"'{name}' has a jitter of {Format(jitter)} ms above its delay of {Format(delay)} ms; a packet cannot arrive before it was sent.");
        }

        var loss = reader.RequireNumber(element, "lossPercent", name, 0, 99);
        var duplicate = reader.RequireNumber(element, "duplicatePercent", name, 0, 100);
        BurstLoss? burst = null;
        if (element.TryGetProperty("burstLoss", out var burstElement))
        {
            var what = $"'{name}.burstLoss'";
            reader.RequireObject(burstElement, what);
            reader.RequireOnly(burstElement, what, "sharePercent", "averageLengthPackets");
            burst = new BurstLoss(
                reader.RequireNumber(burstElement, "sharePercent", what, 0, 100),
                reader.RequireNumber(burstElement, "averageLengthPackets", what, 1, 1_000));
            if (burst.SharePercent <= 0)
            {
                throw reader.Invalid($"{what} needs a share above 0 percent; leave it out for no bursts.");
            }

            if (loss <= 0)
            {
                throw reader.Invalid($"{what} takes a share of the loss, so '{name}' needs a loss above 0 percent.");
            }
        }

        OutageSchedule? outages = null;
        if (element.TryGetProperty("outages", out var outageElement))
        {
            var what = $"'{name}.outages'";
            reader.RequireObject(outageElement, what);
            reader.RequireOnly(outageElement, what, "firstAfterSeconds", "everySeconds", "durationSeconds");
            outages = new OutageSchedule(
                reader.RequireNumber(outageElement, "firstAfterSeconds", what, 0, 86_400),
                reader.RequireNumber(outageElement, "everySeconds", what, 0, 86_400),
                reader.RequireNumber(outageElement, "durationSeconds", what, 0, 86_400));
            if (outages.DurationSeconds <= 0 || outages.EverySeconds <= outages.DurationSeconds)
            {
                throw reader.Invalid($"{what} needs a duration above 0 and a period longer than the duration.");
            }
        }

        return new DirectionProfile(delay, jitter, loss, duplicate, burst, outages);
    }

    private static NetworkProfileVerdict ReadVerdict(StrictReader reader, JsonElement root)
    {
        var element = reader.RequireProperty(root, "verdict", "the profile");
        reader.RequireObject(element, "'verdict'");
        reader.RequireOnly(element, "'verdict'", "allowances", "notApplicable");
        var allowances = new List<NetworkRuleAllowance>();
        foreach (var entry in reader.RequireArray(element, "allowances", "'verdict'"))
        {
            reader.RequireObject(entry, "an allowance");
            reader.RequireOnly(entry, "an allowance", "rule", "add", "reason");
            var rule = reader.RequireString(entry, "rule", "an allowance");
            var add = reader.RequireNumber(entry, "add", $"the allowance of '{rule}'", 0, 1_000_000);
            if (add <= 0)
            {
                throw reader.Invalid($"the allowance of '{rule}' must add more than 0.");
            }

            allowances.Add(new NetworkRuleAllowance(rule, add, reader.RequireString(entry, "reason", $"the allowance of '{rule}'")));
        }

        var notApplicable = new List<NetworkRuleNotApplicable>();
        foreach (var entry in reader.RequireArray(element, "notApplicable", "'verdict'"))
        {
            reader.RequireObject(entry, "a rule that does not apply");
            reader.RequireOnly(entry, "a rule that does not apply", "rule", "reason");
            var rule = reader.RequireString(entry, "rule", "a rule that does not apply");
            notApplicable.Add(new NetworkRuleNotApplicable(rule, reader.RequireString(entry, "reason", $"the rule '{rule}' that does not apply")));
        }

        var twice = allowances.Select(entry => entry.Rule)
            .Concat(notApplicable.Select(entry => entry.Rule))
            .GroupBy(rule => rule, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
        {
            throw reader.Invalid($"the verdict names the rule '{twice.Key}' more than once.");
        }

        return new NetworkProfileVerdict(allowances, notApplicable);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // Strict reading of the profile file: every complaint names the file.
    private sealed class StrictReader(string source)
    {
        public NetworkImpairmentException Invalid(string problem)
        {
            return new NetworkImpairmentException($"The profile '{source}' is invalid: {problem}");
        }

        public void RequireObject(JsonElement element, string what)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"{what} must be a JSON object.");
            }
        }

        public void RequireOnly(JsonElement element, string what, params string[] allowed)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw Invalid($"{what} has the unknown property '{property.Name}'.");
                }
            }
        }

        public JsonElement RequireProperty(JsonElement element, string name, string what)
        {
            return element.TryGetProperty(name, out var value)
                ? value
                : throw Invalid($"{what} needs '{name}'.");
        }

        public JsonElement.ArrayEnumerator RequireArray(JsonElement element, string name, string what)
        {
            var value = RequireProperty(element, name, what);
            return value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : throw Invalid($"'{name}' of {what} must be an array.");
        }

        public string RequireString(JsonElement element, string name, string what)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Invalid($"{what} needs the text '{name}'.");
            }

            return value.GetString()!;
        }

        public double RequireNumber(JsonElement element, string name, string what, double minimum, double maximum)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number)
                || !double.IsFinite(number))
            {
                throw Invalid($"{what} needs the number '{name}'.");
            }

            if (number < minimum || number > maximum)
            {
                throw Invalid($"'{name}' of {what} must lie between {Format(minimum)} and {Format(maximum)}, not {Format(number)}.");
            }

            return number;
        }
    }
}

// What the proxy does to one direction. Every packet waits the delay plus a jitter drawn evenly
// from minus to plus JitterMs. LossPercent is the share of all packets the direction loses; with
// BurstLoss, SharePercent of that loss comes in bursts of AverageLengthPackets on average, and the
// rest falls on single packets. DuplicatePercent of the forwarded packets are sent twice, each copy
// with its own jitter. During an outage every packet is dropped.
public sealed record DirectionProfile(
    double DelayMs,
    double JitterMs,
    double LossPercent,
    double DuplicatePercent,
    BurstLoss? BurstLoss,
    OutageSchedule? Outages);

public sealed record BurstLoss(double SharePercent, double AverageLengthPackets);

// Outages measured from a client's first packet: the first begins FirstAfterSeconds after it,
// and one begins every EverySeconds from then on, each lasting DurationSeconds.
public sealed record OutageSchedule(double FirstAfterSeconds, double EverySeconds, double DurationSeconds)
{
    public bool Contains(double secondsSinceFlowStart)
    {
        if (secondsSinceFlowStart < FirstAfterSeconds)
        {
            return false;
        }

        var intoPeriod = (secondsSinceFlowStart - FirstAfterSeconds) % EverySeconds;
        return intoPeriod < DurationSeconds;
    }
}

// What the load runner's verdict allows under the profile. The proxy reads the section as data;
// the load runner checks the rule names against the quality boundary.
public sealed record NetworkProfileVerdict(
    IReadOnlyList<NetworkRuleAllowance> Allowances,
    IReadOnlyList<NetworkRuleNotApplicable> NotApplicable);

// A rule whose limit grows by Add, in the rule's unit, under the profile.
public sealed record NetworkRuleAllowance(string Rule, double Add, string Reason);

// A rule that the profile's impairment decides by itself, so it does not judge the stack.
public sealed record NetworkRuleNotApplicable(string Rule, string Reason);

public sealed class NetworkImpairmentException(string message) : Exception(message);
