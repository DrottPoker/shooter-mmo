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
}

// A message class that the rule service_log_errors accepts, with the reason it is expected in
// every run. The message matches the start of the class's message.
public sealed record StressLogAllowance(string Service, string Level, string Category, string Message, string Reason)
{
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

    private static readonly Regex Header = new(
        @"^(trce|dbug|info|warn|fail|crit): (.+?)\[\d+\]\s*$",
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
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(allowances);
        var counts = new Dictionary<(string Service, string Level, string Category, string Message), long>();
        var summaries = new List<StressServiceLogFile>();
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

                var key = (service, entry.Level, entry.Category, Classify(entry.Message));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            summaries.Add(new StressServiceLogFile(service, path, entries, warnings, errors, critical));
        }

        var classes = counts
            .Select(pair =>
            {
                var allowance = allowances.FirstOrDefault(candidate =>
                    candidate.Matches(pair.Key.Service, pair.Key.Level, pair.Key.Category, pair.Key.Message));
                return new StressLogClass(
                    pair.Key.Service,
                    pair.Key.Level,
                    pair.Key.Category,
                    pair.Key.Message,
                    pair.Value,
                    allowance is not null,
                    allowance?.Reason);
            })
            .OrderBy(entry => LevelOrder(entry.Level))
            .ThenByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(entry => entry.Category, StringComparer.Ordinal)
            .ThenBy(entry => entry.Message, StringComparer.Ordinal)
            .ToArray();
        return new StressServiceLogSummary(summaries, classes);
    }

    // Every log entry with its level, category, and first message line.
    public static IEnumerable<(string Level, string Category, string Message)> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? level = null;
        string? category = null;
        while (reader.ReadLine() is { } line)
        {
            var header = Header.Match(line);
            if (header.Success)
            {
                if (level is not null)
                {
                    yield return (level, category!, string.Empty);
                }

                level = header.Groups[1].Value switch
                {
                    "warn" => Warning,
                    "fail" => Error,
                    "crit" => Critical,
                    var other => other
                };
                category = header.Groups[2].Value;
                continue;
            }

            if (level is not null)
            {
                yield return (level, category!, line.Trim());
                level = null;
                category = null;
            }
        }

        if (level is not null)
        {
            yield return (level, category!, string.Empty);
        }
    }

    // The first line of every block the runtime wrote to standard error; indented lines, such
    // as a stack trace, belong to the line before them.
    public static IEnumerable<(string Level, string Category, string Message)> ParseStandardError(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                yield return (Error, StandardErrorCategory, line.Trim());
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
        ("simulation-worker", "simulation-worker.log", false),
        ("simulation-worker", "simulation-worker.err.log", true),
        // The network impairment proxy of a run under a network profile writes only its counts to
        // its output; anything on its standard error is an error.
        ("network-proxy", "network-proxy.err.log", true)
    ];

    public static StressRunReport AddLogs(
        StressRunReport report,
        string runFolder,
        IReadOnlyList<StressLogAllowance> allowances)
    {
        ArgumentNullException.ThrowIfNull(report);
        var logs = StressServiceLogs.Read(
            ServiceLogFiles.Select(file => (file.Service, Path.Combine(runFolder, file.File), file.StandardError)),
            allowances);
        var postgresLog = Path.Combine(runFolder, PostgresLogFile);
        return report with
        {
            ServiceLogs = logs,
            PostgresLog = File.Exists(postgresLog) ? ReadPostgresLog(postgresLog) : null
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
