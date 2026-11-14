using System.Globalization;
using System.Text.Json;

namespace Mmorpg.Tools.StackStressGenerator;

public enum StressRuleKind
{
    // Passes when the measured value is at most the limit.
    Maximum,

    // Passes when the measured value is at least the limit.
    Minimum
}

public sealed record StressQualityRule(string Rule, double Limit, string Unit, string Description);

// One judged rule. Measured is null when the rule could not be evaluated, which fails it, or
// when it does not apply to the mode or workload, which the note says and which passes. A
// validity rule judges the generator, not the stack: when one fails, the run measured nothing.
public sealed record StressVerdictEntry(
    string Rule,
    string Comparison,
    double Limit,
    string Unit,
    double? Measured,
    bool Passed,
    string? Note)
{
    public bool Validity { get; init; }
}

// Passed judges the quality rules and Valid the validity rules; a run that is not valid could
// not measure, whatever the quality rules say.
public sealed record StressVerdict(bool Passed, IReadOnlyList<StressVerdictEntry> Rules)
{
    public bool Valid { get; init; } = true;
}

// The quality boundary of tools/load/quality-boundary.json: every rule that the code knows, with
// its limit and unit. The file must name each known rule exactly once with the rule's unit and
// nothing else, so a rule can neither be dropped nor misspelled silently.
public sealed class StressQualityBoundary
{
    public static readonly IReadOnlyDictionary<string, (string Unit, StressRuleKind Kind)> KnownRules =
        new Dictionary<string, (string Unit, StressRuleKind Kind)>(StringComparer.Ordinal)
        {
            ["simulation_tick_p99_ms"] = ("ms", StressRuleKind.Maximum),
            ["fixed_tick_resynchronizations"] = ("count", StressRuleKind.Maximum),
            ["input_acknowledgement_p99_ms"] = ("ms", StressRuleKind.Maximum),
            ["remote_update_rate_p5_hz"] = ("Hz", StressRuleKind.Minimum),
            ["gameplay_operation_p95_ms"] = ("ms", StressRuleKind.Maximum),
            ["bot_failures"] = ("count", StressRuleKind.Maximum),
            ["gameplay_unexpected_failures"] = ("count", StressRuleKind.Maximum),
            ["http_unexpected_failures"] = ("count", StressRuleKind.Maximum),
            ["quota_rejections"] = ("count", StressRuleKind.Maximum),
            ["snapshot_drops"] = ("count", StressRuleKind.Maximum),
            ["owner_snapshot_gaps"] = ("count", StressRuleKind.Maximum),
            ["expired_unreleased_sessions"] = ("count", StressRuleKind.Maximum),
            ["deadlocks"] = ("count", StressRuleKind.Maximum),
            ["item_invariant_violations"] = ("count", StressRuleKind.Maximum),
            ["process_memory_growth"] = ("MiB", StressRuleKind.Maximum),
            ["service_log_errors"] = ("count", StressRuleKind.Maximum),
            ["generator_poll_lateness_p99_ms"] = ("ms", StressRuleKind.Maximum),
            ["generator_route_queue_p99_ms"] = ("ms", StressRuleKind.Maximum),
            ["network_proxy_lateness_p99_ms"] = ("ms", StressRuleKind.Maximum)
        };

    // The rules that judge whether the generator and the network proxy kept up, so that the run
    // measured anything.
    public static readonly IReadOnlySet<string> ValidityRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "generator_poll_lateness_p99_ms",
        "generator_route_queue_p99_ms",
        "network_proxy_lateness_p99_ms"
    };

    // The memory rule judges only a steady interval at least this long; a shorter one is too
    // short for its fifths to tell growth from warm-up and garbage collection.
    public static readonly TimeSpan MemoryRuleMinimumSteady = TimeSpan.FromMinutes(10);

    private StressQualityBoundary(IReadOnlyList<StressQualityRule> rules, IReadOnlyList<StressLogAllowance> logAllowances)
    {
        Rules = rules;
        LogAllowances = logAllowances;
    }

    public IReadOnlyList<StressQualityRule> Rules { get; }

    // The error and critical message classes that service_log_errors accepts, each with its reason.
    public IReadOnlyList<StressLogAllowance> LogAllowances { get; }

    public static StressQualityBoundary Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StressGeneratorOptionException(
                $"The quality boundary '{path}' cannot be read: {exception.Message}");
        }

        return Parse(text, path);
    }

    public static StressQualityBoundary Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw Invalid(source, $"it is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            StrictJson.RequireObject(root, source, "the file");
            StrictJson.RequireOnly(root, source, "the file", "description", "rules");
            StrictJson.RequireString(root, "description", source, "the file");
            if (!root.TryGetProperty("rules", out var rulesElement)
                || rulesElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(source, "'rules' must be an array.");
            }

            var rules = new List<StressQualityRule>();
            var allowances = new List<StressLogAllowance>();
            foreach (var element in rulesElement.EnumerateArray())
            {
                StrictJson.RequireObject(element, source, "a rule");
                var name = StrictJson.RequireString(element, "rule", source, "a rule");
                if (name == "service_log_errors")
                {
                    StrictJson.RequireOnly(element, source, "a rule", "rule", "limit", "unit", "description", "allow");
                    allowances.AddRange(ParseAllowances(element, source));
                }
                else
                {
                    StrictJson.RequireOnly(element, source, "a rule", "rule", "limit", "unit", "description");
                }

                if (!KnownRules.TryGetValue(name, out var known))
                {
                    throw Invalid(source, $"the rule '{name}' is unknown.");
                }

                if (rules.Any(rule => rule.Rule == name))
                {
                    throw Invalid(source, $"the rule '{name}' appears twice.");
                }

                if (!element.TryGetProperty("limit", out var limitElement)
                    || limitElement.ValueKind != JsonValueKind.Number
                    || !double.IsFinite(limitElement.GetDouble()))
                {
                    throw Invalid(source, $"the rule '{name}' needs a numeric limit.");
                }

                var unit = StrictJson.RequireString(element, "unit", source, $"the rule '{name}'");
                if (!string.Equals(unit, known.Unit, StringComparison.Ordinal))
                {
                    throw Invalid(source, $"the rule '{name}' is measured in '{known.Unit}', not '{unit}'.");
                }

                rules.Add(new StressQualityRule(
                    name,
                    limitElement.GetDouble(),
                    unit,
                    StrictJson.RequireString(element, "description", source, $"the rule '{name}'")));
            }

            var missing = KnownRules.Keys.Where(name => rules.All(rule => rule.Rule != name)).ToArray();
            if (missing.Length > 0)
            {
                throw Invalid(source, $"the rules {string.Join(", ", missing)} are missing.");
            }

            return new StressQualityBoundary(rules, allowances);
        }
    }

    // The allow list of service_log_errors: every entry names the service, the level, the logger
    // category, the start of the message class, and the reason it is expected in every run.
    private static IEnumerable<StressLogAllowance> ParseAllowances(JsonElement rule, string source)
    {
        if (!rule.TryGetProperty("allow", out var allow))
        {
            yield break;
        }

        if (allow.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(source, "'allow' of the rule 'service_log_errors' must be an array.");
        }

        foreach (var entry in allow.EnumerateArray())
        {
            StrictJson.RequireObject(entry, source, "an allowed log class");
            StrictJson.RequireOnly(entry, source, "an allowed log class", "service", "level", "category", "message", "reason");
            var level = StrictJson.RequireString(entry, "level", source, "an allowed log class");
            if (level is not (StressServiceLogs.Error or StressServiceLogs.Critical))
            {
                throw Invalid(source, $"an allowed log class has the level '{level}'; only error and critical lines are judged.");
            }

            yield return new StressLogAllowance(
                StrictJson.RequireString(entry, "service", source, "an allowed log class"),
                level,
                StrictJson.RequireString(entry, "category", source, "an allowed log class"),
                StrictJson.RequireString(entry, "message", source, "an allowed log class"),
                StrictJson.RequireString(entry, "reason", source, "an allowed log class"));
        }
    }

    public StressVerdict Evaluate(StressRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var entries = Rules
            .Select(rule => JudgeUnderNetwork(rule, Measure(rule.Rule, report), report.Network) with
            {
                Validity = ValidityRules.Contains(rule.Rule)
            })
            .ToArray();
        return new StressVerdict(
            entries.Where(entry => !entry.Validity).All(entry => entry.Passed),
            entries)
        {
            Valid = entries.Where(entry => entry.Validity).All(entry => entry.Passed)
        };
    }

    // A network profile moves a rule's limit by its allowance, or takes a rule out that its
    // impairment decides by itself; the note says which, and a rule taken out keeps its measured
    // value in the note.
    private static StressVerdictEntry JudgeUnderNetwork(
        StressQualityRule rule,
        Measurement measurement,
        StressNetworkSummary? network)
    {
        if (network?.NotApplicable.FirstOrDefault(entry => entry.Rule == rule.Rule) is { } notApplicable)
        {
            var measured = measurement.NotApplicable || measurement.Value is not { } value
                ? string.Empty
                : Invariant($" Measured {value:0.##} {rule.Unit}{(measurement.Note is null ? string.Empty : "; " + measurement.Note.TrimEnd('.'))}.");
            return Judge(rule, new Measurement(
                null,
                $"Not applicable under the network profile {network.Profile}: {notApplicable.Reason}.{measured}",
                true));
        }

        if (network?.Allowances.FirstOrDefault(entry => entry.Rule == rule.Rule) is { } allowance)
        {
            var entry = Judge(rule with { Limit = rule.Limit + allowance.Add }, measurement);
            var reason = Invariant($"the limit of {rule.Limit:0.##} {rule.Unit} plus {allowance.Add:0.##} {rule.Unit} under the network profile {network.Profile}, {allowance.Reason}");
            return entry with
            {
                Note = entry.Note is null ? char.ToUpperInvariant(reason[0]) + reason[1..] : entry.Note + "; " + reason
            };
        }

        return Judge(rule, measurement);
    }

    private static StressVerdictEntry Judge(StressQualityRule rule, Measurement measurement)
    {
        var kind = KnownRules[rule.Rule].Kind;
        var comparison = kind == StressRuleKind.Maximum ? "<=" : ">=";
        if (measurement.NotApplicable)
        {
            return new StressVerdictEntry(rule.Rule, comparison, rule.Limit, rule.Unit, null, true, measurement.Note);
        }

        if (measurement.Value is not { } value)
        {
            return new StressVerdictEntry(
                rule.Rule,
                comparison,
                rule.Limit,
                rule.Unit,
                null,
                false,
                "Could not be evaluated: " + (measurement.Note ?? "no measurement."));
        }

        var passed = kind == StressRuleKind.Maximum ? value <= rule.Limit : value >= rule.Limit;
        return new StressVerdictEntry(rule.Rule, comparison, rule.Limit, rule.Unit, value, passed, measurement.Note);
    }

    private static Measurement Measure(string rule, StressRunReport report)
    {
        var worker = report.WorkerPerformance;
        var fullStack = report.Configuration.Mode == "full-stack";
        return rule switch
        {
            "simulation_tick_p99_ms" => worker is null
                ? Measurement.Missing("no worker performance file was read.")
                : worker.SteadyCoverageGap is { } tickGap
                    ? Measurement.Missing(tickGap)
                    : worker.Durations.TryGetValue("tick", out var tick)
                        ? Measurement.Of(tick.WorstP99Ms, $"worst of {worker.SteadyIntervals} steady worker intervals; the worker's p99 is a bucket upper bound")
                        : Measurement.Missing(worker.Note ?? "no steady worker interval has tick samples."),
            "fixed_tick_resynchronizations" => worker is null
                ? Measurement.Missing("no worker performance file was read.")
                : worker.SteadyIntervals == 0
                    ? Measurement.Missing(worker.Note ?? "no steady worker interval.")
                    : worker.SteadyCoverageGap is { } resynchronizationGap
                        ? Measurement.Missing(resynchronizationGap)
                        : Measurement.Of(worker.Counters["tickResynchronizations"].Total, $"{worker.SteadyIntervals} steady worker intervals"),
            "input_acknowledgement_p99_ms" => report.Bots.InputAcknowledgementLatency is { } ack
                ? Measurement.Of(ack.P99Ms, "the whole run")
                : Measurement.Missing("no input was acknowledged."),
            "remote_update_rate_p5_hz" => report.Bots.RemoteCadence is { P5Hz: { } p5 } cadence
                ? Measurement.Of(p5, Invariant($"p5 over {cadence.Bots} bots, average {cadence.AverageHz:0.0} Hz, minimum {cadence.MinimumHz:0.0} Hz"))
                : Measurement.Missing("no bot saw a remote player during the steady interval."),
            "gameplay_operation_p95_ms" => MeasureGameplayP95(report),
            "bot_failures" => MeasureBotFailures(report),
            "gameplay_unexpected_failures" => Measurement.Of(report.Gameplay.UnexpectedFailures, null),
            "http_unexpected_failures" => report.FullStack is null
                ? fullStack
                    ? Measurement.Missing("the full-stack summary is missing.")
                    : Measurement.Skip("no HTTP in worker-only mode")
                : MeasureHttpFailures(report.FullStack),
            "quota_rejections" => worker is null
                ? Measurement.Missing("no worker performance file was read.")
                : worker.RunCoverageGap is { } quotaGap
                    ? Measurement.Missing(quotaGap)
                    : Measurement.Of(worker.RunQuotaRejections, $"{worker.RunIntervals} worker intervals of the run"),
            "snapshot_drops" => worker is null
                ? Measurement.Missing("no worker performance file was read.")
                : worker.RunCoverageGap is { } dropGap
                    ? Measurement.Missing(dropGap)
                    : Measurement.Of(worker.RunSnapshotDrops, $"{worker.RunIntervals} worker intervals of the run"),
            "owner_snapshot_gaps" => Measurement.Of(report.Bots.EstimatedMissingSnapshots, null),
            "expired_unreleased_sessions" => !fullStack
                ? Measurement.Skip("no sessions in PostgreSQL in worker-only mode")
                : report.Postgres is { } postgres
                    ? Measurement.Of(postgres.MaximumExpiredUnreleasedSimulationSessions, "the most seen in one sample")
                    : Measurement.Missing("no PostgreSQL samples."),
            "deadlocks" => !fullStack
                ? Measurement.Skip("no PostgreSQL in worker-only mode")
                : report.Postgres is { } deadlockSource
                    ? Measurement.Of(
                        deadlockSource.Deadlocks,
                        report.PostgresLog is { } log
                            ? Invariant($"both statements of every deadlock and every lock wait longer than the deadlock timeout are in {log.Path}, which reports {log.DeadlockReports} deadlocks and {log.LockWaitReports} lock waits")
                            : null)
                    : Measurement.Missing("no PostgreSQL samples."),
            "process_memory_growth" => MeasureMemoryGrowth(report),
            "service_log_errors" => report.ServiceLogs is not { } logs
                ? Measurement.Skip("the service logs are judged once the services stopped, by StackStressGenerator judge, which tools/Test-Load.ps1 runs")
                : Measurement.Of(
                    logs.UnallowedErrors,
                    string.Join(
                        "; ",
                        logs.Classes
                            .Where(entry => !entry.Allowed && entry.Level is StressServiceLogs.Error or StressServiceLogs.Critical)
                            .Take(5)
                            .Select(entry => Invariant($"{entry.Count} {entry.Level} {entry.Service} {entry.Category}: {entry.Message}"))) is { Length: > 0 } classes
                        ? classes
                        : null),
            "item_invariant_violations" => !fullStack
                ? Measurement.Skip("no durable items in worker-only mode")
                : report.Postgres?.ItemInvariants is { } invariants
                    ? Measurement.Of(invariants.Violations, null)
                    : Measurement.Missing("the item invariants were not checked."),
            "generator_poll_lateness_p99_ms" => report.GeneratorTiming?.PollLateness is { } lateness
                ? Measurement.Of(lateness.P99Ms, Invariant($"{lateness.Samples} poll rounds of the steady interval, worst {lateness.MaximumMs:0.0} ms"))
                : Measurement.Skip("no steady interval was measured"),
            "generator_route_queue_p99_ms" => report.GeneratorTiming is not { PlansRoutes: true }
                ? Measurement.Skip("the workload plans no routes")
                : report.GeneratorTiming.RouteQueue is { } queue
                    ? Measurement.Of(queue.P99Ms, Invariant($"{queue.Samples} route plans of the steady interval, worst {queue.MaximumMs:0.0} ms"))
                    : Measurement.Skip("no route was planned in the steady interval"),
            "network_proxy_lateness_p99_ms" => report.Network is not { } network
                ? Measurement.Skip("the run went through no network proxy")
                : network.Proxy is { } proxy
                    ? MeasureProxyLateness(proxy)
                    : network.ProxyNote is { } missing
                        ? Measurement.Missing(missing)
                        : Measurement.Skip("the proxy's counts are read once it stopped, by StackStressGenerator judge, which tools/Test-Load.ps1 runs"),
            _ => throw new InvalidOperationException($"The rule '{rule}' has no measurement.")
        };
    }

    // The last fifth's average working set over the second fifth's, of the worst process; only a
    // planned steady interval of ten minutes or more is judged. The plan decides, not the measured
    // interval, so scheduling jitter at either end cannot take the rule away from a run.
    private static Measurement MeasureMemoryGrowth(StressRunReport report)
    {
        var planned = TimeSpan.FromSeconds(report.Configuration.SteadyDurationSeconds);
        if (planned < MemoryRuleMinimumSteady)
        {
            return Measurement.Skip(Invariant($"a planned steady interval of {planned.TotalMinutes:0.0} minutes is shorter than the {MemoryRuleMinimumSteady.TotalMinutes:0} minutes the rule needs"));
        }

        var required = report.Configuration.Mode == "full-stack"
            ? new[] { "worker", "gameService", "generator" }
            : ["worker", "generator"];
        var trends = report.Soak?.Memory ?? [];
        var missing = required
            .Where(process => trends.All(trend => trend.Process != process || trend.WorkingSetGrowthMiB is null))
            .ToArray();
        if (missing.Length > 0)
        {
            return Measurement.Missing($"the working set of {string.Join(", ", missing)} was not sampled in every fifth of the steady interval.");
        }

        var measured = trends.Where(trend => trend.WorkingSetGrowthMiB is not null).ToArray();
        var worst = measured.MaxBy(trend => trend.WorkingSetGrowthMiB!.Value)!;
        return Measurement.Of(
            worst.WorkingSetGrowthMiB!.Value,
            "worst " + worst.Process + "; " + string.Join(
                ", ",
                measured.Select(trend => Invariant($"{trend.Process} {trend.WorkingSetGrowthMiB:+0.0;-0.0} MiB, slope {trend.WorkingSetSlopeMiBPerHour:+0.0;-0.0} MiB/h"))));
    }

    // The worse direction's lateness against the due time; a proxy that dropped packets because
    // it fell behind measured itself, whatever its lateness.
    private static Measurement MeasureProxyLateness(Mmorpg.Tools.NetworkImpairmentProxy.ProxyCountsReport proxy)
    {
        var overflow = proxy.ClientToServer.DroppedOverflow + proxy.ServerToClient.DroppedOverflow;
        if (overflow > 0)
        {
            return Measurement.Missing(Invariant($"the proxy fell behind and dropped {overflow} packets that its profile would have forwarded."));
        }

        if (proxy.ClientToServer.LatenessMs is not { } up || proxy.ServerToClient.LatenessMs is not { } down)
        {
            return Measurement.Missing("the proxy forwarded no packet in one direction.");
        }

        return Measurement.Of(
            Math.Max(up.P99, down.P99),
            Invariant($"client to server p99 {up.P99:0.000} ms, worst {up.Maximum:0.0} ms; server to client p99 {down.P99:0.000} ms, worst {down.Maximum:0.0} ms; the profile dropped {proxy.ClientToServer.DroppedPercent:0.00} % and {proxy.ServerToClient.DroppedPercent:0.00} %"));
    }

    private static Measurement MeasureGameplayP95(StressRunReport report)
    {
        if (report.Configuration.Workload == "lifecycle")
        {
            return Measurement.Skip("the lifecycle workload has no gameplay operations");
        }

        var operations = report.Gameplay.Operations
            .Where(operation => operation.Value.Requests > 0)
            .ToArray();
        if (operations.Length == 0)
        {
            return Measurement.Missing("no gameplay operation was sent.");
        }

        var unmeasured = operations.Where(operation => operation.Value.Latency is null).Select(operation => operation.Key).ToArray();
        if (unmeasured.Length > 0)
        {
            return Measurement.Missing($"no latency for {string.Join(", ", unmeasured)}.");
        }

        var worst = operations.MaxBy(operation => operation.Value.Latency!.P95Ms);
        return Measurement.Of(
            worst.Value.Latency!.P95Ms,
            "worst kind " + worst.Key + "; " + string.Join(
                ", ",
                operations.Select(operation => Invariant($"{operation.Key} {operation.Value.Latency!.P95Ms:0}"))));
    }

    private static Measurement MeasureBotFailures(StressRunReport report)
    {
        var bots = report.Bots;
        var notCompleted = Math.Max(0, bots.RequestedBots - bots.CompletedBots);
        var notLoggedOut = report.FullStack is { } fullStack
            ? Math.Max(0, fullStack.AdmittedBots - fullStack.LoggedOutBots)
            : 0;
        var codes = bots.FailureCodes.Count == 0
            ? null
            : string.Join(", ", bots.FailureCodes.Select(code => $"{code.Key} {code.Value}"));
        var note = $"{bots.CompletedBots}/{bots.RequestedBots} completed"
            + (report.FullStack is null ? string.Empty : $", {notLoggedOut} not logged out")
            + (codes is null ? string.Empty : $"; {codes}");
        return Measurement.Of(notCompleted + notLoggedOut, note);
    }

    private static Measurement MeasureHttpFailures(StressFullStackSummary fullStack)
    {
        var failures = fullStack.Operations.Where(operation => operation.Value.Failures > 0).ToArray();
        return Measurement.Of(
            failures.Sum(operation => operation.Value.Failures),
            failures.Length == 0
                ? null
                : string.Join(", ", failures.Select(operation => $"{operation.Key} {operation.Value.Failures}")));
    }

    private static string Invariant(FormattableString text)
    {
        return text.ToString(CultureInfo.InvariantCulture);
    }

    private static StressGeneratorOptionException Invalid(string source, string problem)
    {
        return new StressGeneratorOptionException($"The quality boundary '{source}' is invalid: {problem}");
    }

    private sealed record Measurement(double? Value, string? Note, bool NotApplicable)
    {
        public static Measurement Of(double value, string? note) => new(value, note, false);

        public static Measurement Missing(string note) => new(null, note, false);

        public static Measurement Skip(string why) => new(null, "Not applicable: " + why + ".", true);
    }
}

// Strict JSON reading for the load files: an unknown property is an error.
public static class StrictJson
{
    public static void RequireObject(JsonElement element, string source, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StressGeneratorOptionException($"'{source}': {what} must be a JSON object.");
        }
    }

    public static void RequireOnly(JsonElement element, string source, string what, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new StressGeneratorOptionException(
                    $"'{source}': {what} has the unknown property '{property.Name}'.");
            }
        }
    }

    public static string RequireString(JsonElement element, string name, string source, string what)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new StressGeneratorOptionException($"'{source}': {what} needs the text '{name}'.");
        }

        return value.GetString()!;
    }
}
