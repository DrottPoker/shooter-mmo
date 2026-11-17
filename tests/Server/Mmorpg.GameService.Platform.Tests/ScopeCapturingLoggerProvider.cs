using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mmorpg.GameService.Platform.Tests;

internal sealed record CapturedLogEntry(
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> ScopeValues);

// Records every log entry with the values of the scopes around it.
internal sealed class ScopeCapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ScopeCapturingLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class ScopeCapturingLogger(ScopeCapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider.scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new List<KeyValuePair<string, object?>>();
            provider.scopes.ForEachScope(
                (scope, collected) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        collected.AddRange(pairs);
                    }
                },
                values);
            provider.Entries.Enqueue(new CapturedLogEntry(formatter(state, exception), values));
        }
    }
}
