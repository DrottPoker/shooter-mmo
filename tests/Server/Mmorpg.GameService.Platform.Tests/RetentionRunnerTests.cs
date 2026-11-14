using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Retention;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Platform.Tests;

// The retention host's pass with tasks that stand in for the modules' tasks. Every batch still
// runs in a transaction of its own on a real connection.
public sealed class RetentionRunnerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    [PostgresIntegrationFact]
    public async Task ABacklogDrainsInOnePassWhileTheBatchesAreFull()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var backlog = new BacklogTask("backlog", 2_500);
        using var metrics = new RetentionMetrics();

        var pass = await Runner(dataSource, [backlog], metrics).RunPassAsync(CancellationToken.None);

        var task = Assert.Single(pass.Tasks);
        Assert.Equal(3, task.Batches);
        Assert.Equal(2_500, task.Deleted);
        Assert.False(task.MoreDue);
        Assert.False(pass.OutOfBudget);
        Assert.Equal(0, backlog.Remaining);
        Assert.True(backlog.SawItsOwnTransaction);
    }

    [PostgresIntegrationFact]
    public async Task TasksTakeTurnsAndAPassStopsAtItsBudget()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var time = new SteppedTime();
        var first = new BacklogTask("first", 100_000, time, TimeSpan.FromSeconds(10));
        var second = new BacklogTask("second", 100_000, time, TimeSpan.FromSeconds(10));
        var quiet = new BacklogTask("quiet", 10, time, TimeSpan.FromSeconds(10));
        using var metrics = new RetentionMetrics();

        // A 60 second interval with the default budget of 48 seconds: five batches of 10 seconds.
        var pass = await Runner(dataSource, [first, second, quiet], metrics, time)
            .RunPassAsync(CancellationToken.None);

        Assert.True(pass.OutOfBudget);
        Assert.Equal([2, 2, 1], pass.Tasks.Select(task => task.Batches));
        Assert.Equal([true, true, false], pass.Tasks.Select(task => task.MoreDue));
        Assert.Equal(0, quiet.Remaining);
    }

    [PostgresIntegrationFact]
    public async Task ATaskThatFailsDoesNotStopTheOthersAndALockTimeoutIsOnlyAWarning()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var logger = new RecordingLogger();
        var after = new BacklogTask("after", 5);
        using var metrics = new RetentionMetrics();

        var pass = await Runner(
                dataSource,
                [
                    new ThrowingTask("broken", new InvalidOperationException("broken")),
                    new ThrowingTask(
                        "waiting",
                        new PostgresException("lock not available", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable)),
                    after
                ],
                metrics,
                logger: logger)
            .RunPassAsync(CancellationToken.None);

        Assert.True(pass.Tasks.Single(task => task.Task == "broken").Failed);
        Assert.True(pass.Tasks.Single(task => task.Task == "waiting").Failed);
        Assert.Equal(5, pass.Tasks.Single(task => task.Task == "after").Deleted);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("broken", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("waiting", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("waiting", StringComparison.Ordinal));
    }

    // The first batch takes the oldest due rows, so its oldest row says how long rows had been due
    // when the pass began; a pass that finds nothing due has no lag.
    [PostgresIntegrationFact]
    public async Task TheLagIsHowLongTheOldestDueRowHadBeenDueAndTheMetricsKeepItPerTask()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var lags = new Dictionary<string, double>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RetentionMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "game_service.retention.deleted")
            {
                var task = (string)tags.ToArray().Single(tag => tag.Key == "task").Value!;
                deleted[task] = deleted.GetValueOrDefault(task) + value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "game_service.retention.lag")
            {
                lags[(string)tags.ToArray().Single(tag => tag.Key == "task").Value!] = value;
            }
        });
        listener.Start();
        using var metrics = new RetentionMetrics();

        var pass = await Runner(
                dataSource,
                [
                    new BacklogTask("behind", 3, oldestDueAge: Window + TimeSpan.FromSeconds(90)),
                    new BacklogTask("current", 0)
                ],
                metrics)
            .RunPassAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        Assert.Equal(TimeSpan.FromSeconds(90), pass.Tasks.Single(task => task.Task == "behind").Lag);
        Assert.Equal(TimeSpan.Zero, pass.Tasks.Single(task => task.Task == "current").Lag);
        Assert.Equal(3, deleted["behind"]);
        Assert.False(deleted.ContainsKey("current"));
        Assert.Equal(90d, lags["behind"], 3);
        Assert.Equal(0d, lags["current"]);
    }

    [Fact]
    public void TwoTasksWithOneNameAreRefused()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        using var metrics = new RetentionMetrics();

        var exception = Assert.Throws<InvalidOperationException>(
            () => Runner(dataSource, [new BacklogTask("twice", 1), new BacklogTask("twice", 1)], metrics));

        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    // A pass that outlasts the interval delays the next one; two passes never run at once.
    [PostgresIntegrationFact]
    public async Task TheHostRunsOnePassAtATime()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var slow = new SlowTask(TimeSpan.FromMilliseconds(1_500));
        using var metrics = new RetentionMetrics();
        var options = RetentionOptions.Default with { Interval = TimeSpan.FromSeconds(1), PassBudgetPercent = 100 };
        var runner = new RetentionRunner(
            dataSource,
            [slow],
            options,
            metrics,
            TimeProvider.System,
            NullLogger<RetentionRunner>.Instance);
        using var host = new RetentionHostedService(runner, options, TimeProvider.System);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await host.StopAsync(CancellationToken.None);

        Assert.True(slow.Calls >= 2, $"The host ran {slow.Calls} passes.");
        Assert.Equal(1, slow.MostAtOnce);
    }

    private static RetentionRunner Runner(
        NpgsqlDataSource dataSource,
        IReadOnlyList<IRetentionTask> tasks,
        RetentionMetrics metrics,
        TimeProvider? time = null,
        ILogger<RetentionRunner>? logger = null)
    {
        return new RetentionRunner(
            dataSource,
            tasks,
            RetentionOptions.Default,
            metrics,
            time ?? TimeProvider.System,
            logger ?? NullLogger<RetentionRunner>.Instance);
    }

    // Deletes up to a batch from a backlog of rows, and moves the clock by its step for each batch.
    private sealed class BacklogTask(
        string name,
        int backlog,
        SteppedTime? time = null,
        TimeSpan step = default,
        TimeSpan? oldestDueAge = null) : IRetentionTask
    {
        public string Name => name;

        public RetentionClass Class => RetentionClass.ItemHistory;

        public int Remaining { get; private set; } = backlog;

        public bool SawItsOwnTransaction { get; private set; }

        public async Task<RetentionBatchResult> DeleteDueAsync(
            PostgresTransactionScope scope,
            RetentionBatch batch,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("select current_setting('lock_timeout');", scope.Connection, scope.Transaction);
            SawItsOwnTransaction = (string?)await command.ExecuteScalarAsync(cancellationToken) == "1s";
            time?.Advance(step);
            var deleted = Math.Min(batch.Size, Remaining);
            Remaining -= deleted;
            return new RetentionBatchResult(deleted, deleted == 0 ? null : oldestDueAge ?? batch.Window);
        }
    }

    private sealed class ThrowingTask(string name, Exception exception) : IRetentionTask
    {
        public string Name => name;

        public RetentionClass Class => RetentionClass.SessionHistory;

        public Task<RetentionBatchResult> DeleteDueAsync(
            PostgresTransactionScope scope,
            RetentionBatch batch,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class SlowTask(TimeSpan duration) : IRetentionTask
    {
        private int running;
        private int mostAtOnce;
        private int calls;

        public string Name => "slow";

        public RetentionClass Class => RetentionClass.SessionHistory;

        public int Calls => Volatile.Read(ref calls);

        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        public async Task<RetentionBatchResult> DeleteDueAsync(
            PostgresTransactionScope scope,
            RetentionBatch batch,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            try
            {
                await Task.Delay(duration, cancellationToken);
                return RetentionBatchResult.None;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    // A clock that moves only when a task says so.
    private sealed class SteppedTime : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan step) => Interlocked.Add(ref ticks, step.Ticks);
    }

    private sealed class RecordingLogger : ILogger<RetentionRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
