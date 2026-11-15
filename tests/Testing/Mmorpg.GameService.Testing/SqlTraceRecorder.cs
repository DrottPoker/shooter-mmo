using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Mmorpg.GameService.Testing;

// Captures the statements and transaction boundaries that Npgsql logs for one data
// source. Npgsql logs each command before it runs, so a failing statement is captured
// too. Connector ids are replaced by c1, c2, ... in order of first use.
//
// Npgsql logs one command with several statements and an NpgsqlBatch alike. A data source
// whose tracing the recorder configures tells them apart: a command with several statements
// is a "batch N" line with its statements below it, and an NpgsqlBatch is one "sql" line per
// statement, as each statement was when it travelled alone. The round trip lines add a
// "-- round trip" line before every round trip: a command, an NpgsqlBatch, a commit, or a
// rollback. A begin travels with the round trip after it.
internal sealed partial class SqlTraceRecorder : ILoggerProvider
{
    public const string RoundTripLine = "-- round trip";

    private const string CommandCategory = "Npgsql.Command";
    private const string TransactionCategory = "Npgsql.Transaction";
    private const int ExecutingCommandEventId = 2000;
    private const int StartedTransactionEventId = 30000;
    private const int CommittedTransactionEventId = 30001;
    private const int RolledBackTransactionEventId = 30002;

    // Npgsql calls a batch filter only while something listens to its activity source. This
    // listener samples nothing, so it creates no activity.
    private static readonly ActivityListener NpgsqlListener = CreateNpgsqlListener();

    private readonly Lock gate = new();
    private readonly List<Entry> entries = [];
    private readonly Dictionary<int, string> connectorNames = [];
    private bool recording;
    private string? batchMismatch;

    // Runs synchronously on the executing thread right after a statement is recorded.
    public Action<string>? OnStatement { get; set; }

    public static ILoggerFactory CreateLoggerFactory(SqlTraceRecorder recorder)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter((category, _) =>
                category is CommandCategory or TransactionCategory);
            builder.AddProvider(recorder);
        });
    }

    // Lets the recorder see which logged batches are NpgsqlBatch round trips. Npgsql runs the
    // batch filter right after it logged the batch and before it sends it.
    public void ConfigureTracing(NpgsqlTracingOptionsBuilder tracing)
    {
        ArgumentNullException.ThrowIfNull(tracing);
        GC.KeepAlive(NpgsqlListener);
        tracing.ConfigureBatchFilter(batch =>
        {
            MarkBatch(batch.BatchCommands.Count);
            return false;
        });
    }

    public void Start()
    {
        lock (gate)
        {
            entries.Clear();
            connectorNames.Clear();
            batchMismatch = null;
            recording = true;
        }
    }

    public IReadOnlyList<string> Stop()
    {
        lock (gate)
        {
            recording = false;
            return Render(entries).Lines;
        }
    }

    // Stops like Stop and fails when an NpgsqlBatch could not be matched with its log entry.
    public SqlTrace StopTrace()
    {
        lock (gate)
        {
            recording = false;
            if (batchMismatch is not null)
            {
                throw new InvalidOperationException(batchMismatch);
            }

            return Render(entries);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RecordingLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    public static string Normalize(string commandText)
    {
        return WhitespacePattern().Replace(commandText, " ").Trim();
    }

    private static SqlTrace Render(IReadOnlyList<Entry> entries)
    {
        var lines = new List<string>();
        var roundTripLines = new List<string>();
        var beginPending = new HashSet<string>(StringComparer.Ordinal);
        var roundTrips = 0;
        foreach (var entry in entries)
        {
            var entryLines = LinesOf(entry);
            lines.AddRange(entryLines);
            if (entry.Kind == EntryKind.Begin)
            {
                beginPending.Add(entry.Connector);
                roundTripLines.Add(RoundTripLine);
                roundTrips++;
            }
            else if (!beginPending.Remove(entry.Connector))
            {
                roundTripLines.Add(RoundTripLine);
                roundTrips++;
            }

            roundTripLines.AddRange(entryLines);
        }

        return new SqlTrace(lines, roundTripLines, roundTrips);
    }

    private static IEnumerable<string> LinesOf(Entry entry)
    {
        return entry.Kind switch
        {
            EntryKind.Begin => [$"{entry.Connector} begin {entry.Detail}"],
            EntryKind.Commit => [$"{entry.Connector} commit"],
            EntryKind.Rollback => [$"{entry.Connector} rollback"],
            _ when entry.Statements.Length == 1 || entry.IsNpgsqlBatch =>
                entry.Statements.Select(statement => $"{entry.Connector} sql {statement}"),
            _ => entry.Statements
                .Select(statement => $"  > {statement}")
                .Prepend($"{entry.Connector} batch {entry.Statements.Length}")
        };
    }

    private void MarkBatch(int commandCount)
    {
        lock (gate)
        {
            if (!recording || commandCount < 2)
            {
                return;
            }

            var last = entries.Count == 0 ? null : entries[^1];
            if (last is null
                || last.Kind != EntryKind.Statements
                || last.IsNpgsqlBatch
                || last.Statements.Length != commandCount)
            {
                batchMismatch ??= $"An NpgsqlBatch of {commandCount} statements did not follow its log entry.";
                return;
            }

            entries[^1] = last with { IsNpgsqlBatch = true };
        }
    }

    private void Record(
        string category,
        EventId eventId,
        IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        string? statement = null;
        lock (gate)
        {
            if (!recording)
            {
                return;
            }

            var connector = NameConnector(state);
            if (string.Equals(category, TransactionCategory, StringComparison.Ordinal))
            {
                var entry = eventId.Id switch
                {
                    StartedTransactionEventId => new Entry(connector, EntryKind.Begin, Value(state, "IsolationLevel"), []),
                    CommittedTransactionEventId => new Entry(connector, EntryKind.Commit, null, []),
                    RolledBackTransactionEventId => new Entry(connector, EntryKind.Rollback, null, []),
                    _ => null
                };
                if (entry is not null)
                {
                    entries.Add(entry);
                }

                return;
            }

            if (!string.Equals(category, CommandCategory, StringComparison.Ordinal)
                || eventId.Id != ExecutingCommandEventId)
            {
                return;
            }

            if (TryGetValue(state, "CommandText", out var commandText))
            {
                statement = Normalize(Convert.ToString(commandText) ?? string.Empty);
                entries.Add(new Entry(connector, EntryKind.Statements, null, [statement]));
            }
            else if (TryGetValue(state, "BatchCommands", out var batchCommands)
                && batchCommands is IEnumerable commands)
            {
                var texts = commands
                    .Cast<object?>()
                    .Select(command => Normalize(Convert.ToString(command) ?? string.Empty))
                    .ToArray();
                entries.Add(new Entry(connector, EntryKind.Statements, null, texts));
                statement = string.Join(" ; ", texts);
            }
        }

        if (statement is not null)
        {
            OnStatement?.Invoke(statement);
        }
    }

    private string NameConnector(IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        if (!TryGetValue(state, "ConnectorId", out var value) || value is not int connectorId)
        {
            return "c?";
        }

        if (!connectorNames.TryGetValue(connectorId, out var name))
        {
            name = $"c{connectorNames.Count + 1}";
            connectorNames.Add(connectorId, name);
        }

        return name;
    }

    private static string Value(IReadOnlyList<KeyValuePair<string, object?>> state, string key)
    {
        return TryGetValue(state, key, out var value) ? Convert.ToString(value) ?? "null" : "?";
    }

    private static bool TryGetValue(
        IReadOnlyList<KeyValuePair<string, object?>> state,
        string key,
        out object? value)
    {
        foreach (var pair in state)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static ActivityListener CreateNpgsqlListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "Npgsql", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private enum EntryKind
    {
        Begin,
        Commit,
        Rollback,
        Statements
    }

    private sealed record Entry(string Connector, EntryKind Kind, string? Detail, string[] Statements)
    {
        public bool IsNpgsqlBatch { get; init; }
    }

    private sealed class RecordingLogger(SqlTraceRecorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                recorder.Record(category, eventId, values);
            }
        }
    }
}

// The lines of one recording: the statement and transaction lines, the same lines with a
// "-- round trip" line before every round trip, and the number of round trips.
internal sealed record SqlTrace(
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> RoundTripLines,
    int RoundTrips);
