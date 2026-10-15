using Microsoft.Extensions.Logging;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Records log entries with their structured properties for telemetry assertions.
internal sealed class StructuredLogger<TCategory> : ILogger<TCategory>
{
    private readonly Lock gate = new();
    private readonly List<StructuredLogEntry> entries = [];

    public IReadOnlyList<StructuredLogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

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
        var properties = state is IReadOnlyList<KeyValuePair<string, object?>> values
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        lock (gate)
        {
            entries.Add(new StructuredLogEntry(
                logLevel,
                eventId,
                exception,
                formatter(state, exception),
                properties));
        }
    }
}

internal sealed record StructuredLogEntry(
    LogLevel Level,
    EventId EventId,
    Exception? Exception,
    string Message,
    IReadOnlyDictionary<string, object?> Properties)
{
    public string? Template => Properties.TryGetValue("{OriginalFormat}", out var value)
        ? value as string
        : null;
}

// Records the category of every logger it creates, for category assertions.
internal sealed class LogCategoryRecorder : ILoggerProvider
{
    private readonly Lock gate = new();
    private readonly List<(string Category, LogLevel Level)> entries = [];

    public IReadOnlyList<(string Category, LogLevel Level)> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CategoryLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class CategoryLogger(LogCategoryRecorder provider, string category) : ILogger
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
            lock (provider.gate)
            {
                provider.entries.Add((category, logLevel));
            }
        }
    }
}
