using Mmorpg.Tools.NetworkImpairmentProxy;

namespace Mmorpg.Tools.StackStressGenerator;

// The network profile a run went through, with what its verdict allows under it, and once the
// proxy stopped, the proxy's counts. A run without a profile has none.
public sealed record StressNetworkSummary(
    string Profile,
    string Description,
    long Seed,
    double RoundTripMs,
    double RoundTripJitterMs,
    IReadOnlyList<NetworkRuleAllowance> Allowances,
    IReadOnlyList<NetworkRuleNotApplicable> NotApplicable)
{
    // What the proxy did, which StackStressGenerator judge reads once it stopped.
    public ProxyCountsReport? Proxy { get; init; }

    // Why the judge found no counts, which makes the run unable to measure.
    public string? ProxyNote { get; init; }

    public static StressNetworkSummary Create(NetworkProfile profile, long seed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new StressNetworkSummary(
            profile.Name,
            profile.Description,
            seed,
            profile.RoundTripMs,
            profile.RoundTripJitterMs,
            profile.Verdict.Allowances,
            profile.Verdict.NotApplicable);
    }
}

// The network profiles of tools/load/network-profiles as the load runner uses them: a profile's
// verdict section may name only rules of the quality boundary, never a validity rule, and an
// allowance only on a rule with a maximum.
public static class StressNetworkProfiles
{
    // The proxy's counts in the run folder, which the proxy writes when it stops.
    public const string CountsFileName = "network-proxy.json";

    // The proxy listens this far above the worker's port, so both ports of a run are known.
    public const int ProxyPortOffset = 10;

    public static string FolderPath(string repositoryRoot)
    {
        return Path.Combine(repositoryRoot, "tools", "load", NetworkProfile.FolderName);
    }

    // The profile a name means, or null for none.
    public static NetworkProfile? LoadNamed(string name, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name == NetworkProfile.None)
        {
            return null;
        }

        if (!NetworkProfile.IsValidName(name))
        {
            throw new StressGeneratorOptionException(
                $"'{name}' is not a network profile name: lowercase letters, digits, and dashes, or '{NetworkProfile.None}'.");
        }

        var path = Path.Combine(FolderPath(repositoryRoot), name + ".json");
        if (!File.Exists(path))
        {
            var folder = FolderPath(repositoryRoot);
            string[] known = Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*.json").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal)]
                : [];
            throw new StressGeneratorOptionException(
                $"The network profile '{name}' does not exist. Profiles: {NetworkProfile.None}, {string.Join(", ", known)}.");
        }

        return Load(path);
    }

    public static NetworkProfile Load(string path)
    {
        NetworkProfile profile;
        try
        {
            profile = NetworkProfile.Load(path);
        }
        catch (NetworkImpairmentException exception)
        {
            throw new StressGeneratorOptionException(exception.Message);
        }

        Validate(profile);
        return profile;
    }

    public static void Validate(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var rule in profile.Verdict.Allowances.Select(allowance => allowance.Rule)
            .Concat(profile.Verdict.NotApplicable.Select(entry => entry.Rule)))
        {
            if (!StressQualityBoundary.KnownRules.ContainsKey(rule))
            {
                throw Invalid(profile, $"the verdict names the rule '{rule}', which the quality boundary does not know.");
            }

            if (StressQualityBoundary.ValidityRules.Contains(rule))
            {
                throw Invalid(profile, $"the validity rule '{rule}' judges the tools, not the network, so a profile cannot change it.");
            }
        }

        foreach (var allowance in profile.Verdict.Allowances)
        {
            if (StressQualityBoundary.KnownRules[allowance.Rule].Kind != StressRuleKind.Maximum)
            {
                throw Invalid(profile, $"the rule '{allowance.Rule}' has a minimum, so it takes no allowance.");
            }
        }
    }

    // Adds the proxy's counts from the run folder; their absence makes the proxy's validity rule
    // unable to measure.
    public static StressRunReport AddProxyCounts(StressRunReport report, string runFolder)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Network is not { } network)
        {
            return report;
        }

        var path = Path.Combine(runFolder, CountsFileName);
        if (!File.Exists(path))
        {
            return report with
            {
                Network = network with { Proxy = null, ProxyNote = $"the proxy wrote no counts to {path}; it did not stop on Ctrl+C." }
            };
        }

        try
        {
            return report with
            {
                Network = network with { Proxy = ProxyCountsReport.Parse(File.ReadAllText(path)), ProxyNote = null }
            };
        }
        catch (System.Text.Json.JsonException exception)
        {
            return report with
            {
                Network = network with { Proxy = null, ProxyNote = $"the proxy's counts in {path} cannot be read: {exception.Message}" }
            };
        }
    }

    private static StressGeneratorOptionException Invalid(NetworkProfile profile, string problem)
    {
        return new StressGeneratorOptionException($"The network profile '{profile.Name}' is invalid for the load runner: {problem}");
    }
}
