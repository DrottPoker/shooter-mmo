using System.Globalization;
using System.Text.RegularExpressions;

namespace Mmorpg.Tools.StackStressGenerator;

// One message class of a service log: the service, the level, the logger category, and the
// message with its varying parts, such as ids and numbers, replaced, cut to its first words.
public sealed record StressLogClass(
    string Service,
    string Level,
    string Category,
    string Message,
    long Count,
    bool Allowed,
    string? AllowReason);

public sealed record StressServiceLogFile(string Service, string Path, long Entries, long Warnings, long Errors, long Critical);

// The warning, error, and critical lines of the service logs of a run, by message class.
public sealed record StressServiceLogSummary(
    IReadOnlyList<StressServiceLogFile> Files,
    IReadOnlyList<StressLogClass> Classes)
{
    public long UnallowedErrors => Classes
        .Where(entry => !entry.Allowed && entry.Level is StressServiceLogs.Error or StressServiceLogs.Critical)
        .Sum(entry => entry.Count);

    // The worker's failed registry heartbeats, by its warning line.
    public long WorkerHeartbeatFailures { get; init; }

    // The worker's warning, error, and critical lines that name auth_service_timeout: a call to
    // GameService that did not answer within the worker's timeout.
    public long AuthServiceTimeouts { get; init; }

    // The same lines inside a GameService outage that an allowance for the outage accepts; the
    // two counts above leave them out.
    public long WorkerHeartbeatFailuresDuringOutage { get; init; }

    public long AuthServiceTimeoutsDuringOutage { get; init; }

    // In a run with an outage and timestamped logs: when the worker first logged that GameService
    // does not answer after the stop, and then that it answers again.
    public DateTime? WorkerOutageNoticedAt { get; init; }

    public DateTime? WorkerReturnNoticedAt { get; init; }
}

// A message class that the rule service_log_errors accepts, with the reason it is expected in
// every run. The message matches the start of the class's message. An allowance for the outage
// accepts its class only in a run with a GameService outage and only inside its window, and may
// name warning lines, which worker_heartbeat_failures and auth_service_timeouts then leave out.
public sealed record StressLogAllowance(string Service, string Level, string Category, string Message, string Reason)
{
    public bool DuringOutage { get; init; }

    public bool Matches(string service, string level, string category, string message)
    {
        return string.Equals(Service, service, StringComparison.Ordinal)
            && string.Equals(Level, level, StringComparison.Ordinal)
            && string.Equals(Category, category, StringComparison.Ordinal)
            && message.StartsWith(Message, StringComparison.Ordinal);
    }
}

// What the PostgreSQL log of the run says, and where it is.
public sealed record StressPostgresLogSummary(string Path, long DeadlockReports, long LockWaitReports);

// Reads the console logs of GameService and SimulationWorker, as the hosting's simple console
// logger writes them: a line "<level>: <category>[<event>]" and the message indented below it.
// Their standard error files hold what the runtime itself wrote, such as an unhandled
// exception, and every first line there counts as an error.
public static class StressServiceLogs
{
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";
    public const string StandardErrorCategory = "stderr";

    // A message class keeps this many characters of its normalized first line.
    public const int MessageLength = 120;

    public const string WorkerService = "simulation-worker";
    public const string WorkerHeartbeatFailureMessage = "Simulation worker registry heartbeat failed";
    public const string AuthServiceTimeoutCode = "auth_service_timeout";

    // The worker's lines when GameService stops and starts answering its registry heartbeat.
    public const string WorkerOutageMessage = "GameService does not answer the registry heartbeat";
    public const string WorkerReturnMessage = "GameService answers the registry heartbeat again";

    // A header line, with the UTC time in front that a run with an outage asks the console
    // logger for.
    private static readonly Regex Header = new(
        @"^(?:(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)\s*)?(trce|dbug|info|warn|fail|crit): (.+?)\[\d+\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Guid = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LongHex = new(
        @"\b[0-9a-fA-F]{16,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Number = new(
        @"\d+([.,]\d+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static StressServiceLogSummary Read(
        IEnumerable<(string Service, string Path, bool StandardError)> files,
        IReadOnlyList<StressLogAllowance> allowances)
    {
        return Read(files, allowances, null);
    }

    // The outage window, when the run had one, is where an allowance for the outage holds; the
    // outage's stop is where the search for the worker's two lines starts.
    public static StressServiceLogSummary Read(
        IEnumerable<(string Service, string Path, bool StandardError)> files,
        IReadOnlyList<StressLogAllowance> allowances,
        (DateTime Start, DateTime End)? outageWindow)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(allowances);
        var counts = new Dictionary<(string Service, string Level, string Category, string Message, StressLogAllowance? Allowance), long>();
        var summaries = new List<StressServiceLogFile>();
        long heartbeatFailures = 0;
        long authServiceTimeouts = 0;
        long heartbeatFailuresDuringOutage = 0;
        long authServiceTimeoutsDuringOutage = 0;
        DateTime? outageNoticedAt = null;
        DateTime? returnNoticedAt = null;
        foreach (var (service, path, standardError) in files)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            long entries = 0, warnings = 0, errors = 0, critical = 0;
            foreach (var entry in standardError ? ParseStandardError(reader) : Parse(reader))
            {
                entries++;
                if (service == WorkerService && outageWindow is { } stop && entry.At is { } time && time >= stop.Start)
                {
                    // The return line is information, so it is looked for before the levels.
                    if (outageNoticedAt is null && entry.Message.StartsWith(WorkerOutageMessage, StringComparison.Ordinal))
                    {
                        outageNoticedAt = time;
                    }
                    else if (outageNoticedAt is not null
                        && returnNoticedAt is null
                        && entry.Message.StartsWith(WorkerReturnMessage, StringComparison.Ordinal))
                    {
                        returnNoticedAt = time;
                    }
                }

                switch (entry.Level)
                {
                    case Warning:
                        warnings++;
                        break;
                    case Error:
                        errors++;
                        break;
                    case Critical:
                        critical++;
                        break;
                    default:
                        continue;
                }

                var message = Classify(entry.Message);
                var allowance = allowances.FirstOrDefault(candidate =>
                    candidate.Matches(service, entry.Level, entry.Category, message)
                    && (!candidate.DuringOutage
                        || (outageWindow is { } window && entry.At is { } at && at >= window.Start && at <= window.End)));
                var key = (service, entry.Level, entry.Category, message, allowance);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (service == WorkerService)
                {
                    var outageLine = allowance?.DuringOutage == true;
                    if (entry.Message.StartsWith(WorkerHeartbeatFailureMessage, StringComparison.Ordinal))
                    {
                        if (outageLine)
                        {
                            heartbeatFailuresDuringOutage++;
                        }
                        else
                        {
                            heartbeatFailures++;
                        }
                    }

                    if (entry.Message.Contains(AuthServiceTimeoutCode, StringComparison.Ordinal))
                    {
                        if (outageLine)
                        {
                            authServiceTimeoutsDuringOutage++;
                        }
                        else
                        {
                            authServiceTimeouts++;
                        }
                    }
                }
            }

            summaries.Add(new StressServiceLogFile(service, path, entries, warnings, errors, critical));
        }

        var classes = counts
            .Select(pair => new StressLogClass(
                pair.Key.Service,
                pair.Key.Level,
                pair.Key.Category,
                pair.Key.Message,
                pair.Value,
                pair.Key.Allowance is not null,
                pair.Key.Allowance?.Reason))
            .OrderBy(entry => LevelOrder(entry.Level))
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(entry => entry.Category, StringComparer.Ordinal)
            .ThenBy(entry => entry.Message, StringComparer.Ordinal)
            .ToArray();
        return new StressServiceLogSummary(summaries, classes)
        {
            WorkerHeartbeatFailures = heartbeatFailures,
            AuthServiceTimeouts = authServiceTimeouts,
            WorkerHeartbeatFailuresDuringOutage = heartbeatFailuresDuringOutage,
            AuthServiceTimeoutsDuringOutage = authServiceTimeoutsDuringOutage,
            WorkerOutageNoticedAt = outageNoticedAt,
            WorkerReturnNoticedAt = returnNoticedAt
        };
    }

    // Every log entry with its level, category, first message line, and its UTC time when the
    // logger wrote one.
    public static IEnumerable<(string Level, string Category, string Message, DateTime? At)> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? level = null;
        string? category = null;
        DateTime? at = null;
        while (reader.ReadLine() is { } line)
        {
            var header = Header.Match(line);
            if (header.Success)
            {
                if (level is not null)
                {
                    yield return (level, category!, string.Empty, at);
                }

                at = header.Groups[1].Success
                    && DateTime.TryParseExact(
                        header.Groups[1].Value,
                        "yyyy-MM-ddTHH:mm:ss.fffZ",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var time)
                    ? time
                    : null;
                level = header.Groups[2].Value switch
                {
                    "warn" => Warning,
                    "fail" => Error,
                    "crit" => Critical,
                    var other => other
                };
                category = header.Groups[3].Value;
                continue;
            }

            if (level is not null)
            {
                yield return (level, category!, line.Trim(), at);
                level = null;
                category = null;
                at = null;
            }
        }

        if (level is not null)
        {
            yield return (level, category!, string.Empty, at);
        }
    }

    // The first line of every block the runtime wrote to standard error; indented lines, such
    // as a stack trace, belong to the line before them. Standard error has no times.
    public static IEnumerable<(string Level, string Category, string Message, DateTime? At)> ParseStandardError(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                yield return (Error, StandardErrorCategory, line.Trim(), null);
            }
        }
    }

    // The message with ids, long hexadecimal values, and numbers replaced, cut to its first
    // words, so the lines of one message template fall into one class.
    public static string Classify(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = Guid.Replace(message, "<id>");
        text = LongHex.Replace(text, "<id>");
        text = Number.Replace(text, "#");
        return text.Length <= MessageLength ? text : text[..MessageLength];
    }

    private static int LevelOrder(string level)
    {
        return level switch
        {
            Critical => 0,
            Error => 1,
            Warning => 2,
            _ => 3
        };
    }
}

// Adds what the logs of a finished run say to its report: the service logs by message class, and
// the PostgreSQL log the script kept, with its deadlock and lock wait reports.
public static class StressRunJudge
{
    public const string PostgresLogFile = "postgres.log";

    public static readonly IReadOnlyList<(string Service, string File, bool StandardError)> ServiceLogFiles =
    [
        ("game-service", "game-service.log", false),
        ("game-service", "game-service.err.log", true),
        // A run with a GameService outage starts GameService a second time, with logs of its own.
        ("game-service", "game-service-2.log", false),
        ("game-service", "game-service-2.err.log", true),
        ("simulation-worker", "simulation-worker.log", false),
        ("simulation-worker", "simulation-worker.err.log", true),
        // The network impairment proxy of a run under a network profile writes only its counts to
        // its output; anything on its standard error is an error.
        ("network-proxy", "network-proxy.err.log", true)
    ];

    // The outage's log window ends the return limit after GameService was ready, the latest
    // return that the rule outage_return_seconds accepts.
    public static StressRunReport AddLogs(
        StressRunReport report,
        string runFolder,
        IReadOnlyList<StressLogAllowance> allowances,
        double outageReturnLimitSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(report);
        var window = report.Outage?.LogWindow(outageReturnLimitSeconds);
        var logs = StressServiceLogs.Read(
            ServiceLogFiles.Select(file => (file.Service, Path.Combine(runFolder, file.File), file.StandardError)),
            allowances,
            window);
        var postgresLog = Path.Combine(runFolder, PostgresLogFile);
        return report with
        {
            ServiceLogs = logs,
            PostgresLog = File.Exists(postgresLog) ? ReadPostgresLog(postgresLog) : null,
            Outage = report.Outage is { } outage ? AddWorkerNotices(outage, logs) : null
        };
    }

    // When the worker noticed the outage and the return, against the stop and the readiness
    // that the script measured.
    private static StressOutageSummary AddWorkerNotices(StressOutageSummary outage, StressServiceLogSummary logs)
    {
        if (outage.StoppedAt is not { } stopped || outage.ReadyAt is not { } ready)
        {
            return outage with { WorkerLogNote = "the run has no outage window to compare the worker's log with" };
        }

        return outage with
        {
            WorkerNoticedOutageSeconds = logs.WorkerOutageNoticedAt is { } noticed ? (noticed - stopped).TotalSeconds : null,
            WorkerNoticedReturnSeconds = logs.WorkerReturnNoticedAt is { } returned ? (returned - ready).TotalSeconds : null,
            WorkerLogNote = logs.WorkerOutageNoticedAt is null
                ? $"the worker's log has no timed line '{StressServiceLogs.WorkerOutageMessage}' after the stop"
                : logs.WorkerReturnNoticedAt is null
                    ? $"the worker's log has no timed line '{StressServiceLogs.WorkerReturnMessage}' after its outage line"
                    : null
        };
    }

    // PostgreSQL reports a deadlock with "deadlock detected" and both statements in its detail,
    // and with log_lock_waits every wait longer than deadlock_timeout as "still waiting for".
    public static StressPostgresLogSummary ReadPostgresLog(string path)
    {
        long deadlocks = 0;
        long lockWaits = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Contains("deadlock detected", StringComparison.Ordinal))
            {
                deadlocks++;
            }
            else if (line.Contains("still waiting for", StringComparison.Ordinal))
            {
                lockWaits++;
            }
        }

        return new StressPostgresLogSummary(path, deadlocks, lockWaits);
    }
}
