using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Captures the statements and transaction boundaries that Npgsql logs for one data
// source. Npgsql logs each command before it runs, so a failing statement is captured
// too. Connector ids are replaced by c1, c2, ... in order of first use.
internal sealed partial class SqlTraceRecorder : ILoggerProvider
{
    private const string CommandCategory = "Npgsql.Command";
    private const string TransactionCategory = "Npgsql.Transaction";
    private const int ExecutingCommandEventId = 2000;
    private const int StartedTransactionEventId = 30000;
    private const int CommittedTransactionEventId = 30001;
    private const int RolledBackTransactionEventId = 30002;

    private readonly Lock gate = new();
    private readonly List<string> lines = [];
    private readonly Dictionary<int, string> connectorNames = [];
    private bool recording;

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

    public void Start()
    {
        lock (gate)
        {
            lines.Clear();
            connectorNames.Clear();
            recording = true;
        }
    }

    public IReadOnlyList<string> Stop()
    {
        lock (gate)
        {
            recording = false;
            return lines.ToArray();
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
                var line = eventId.Id switch
                {
                    StartedTransactionEventId => $"{connector} begin {Value(state, "IsolationLevel")}",
                    CommittedTransactionEventId => $"{connector} commit",
                    RolledBackTransactionEventId => $"{connector} rollback",
                    _ => null
                };
                if (line is not null)
                {
                    lines.Add(line);
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
                lines.Add($"{connector} sql {statement}");
            }
            else if (TryGetValue(state, "BatchCommands", out var batchCommands)
                && batchCommands is IEnumerable commands)
            {
                var texts = commands
                    .Cast<object?>()
                    .Select(command => Normalize(Convert.ToString(command) ?? string.Empty))
                    .ToArray();
                lines.Add($"{connector} batch {texts.Length}");
                lines.AddRange(texts.Select(text => $"  > {text}"));
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

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
