using System.Data;
using System.Globalization;
using Dapper;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Platform.Retention;

// What one task did in a pass: its batches, the rows they deleted, its time, the lag when its
// first batch measured one, and whether rows may still be due because it failed or the pass ended.
internal sealed record RetentionTaskPass(
    string Task,
    int Batches,
    int Deleted,
    TimeSpan Duration,
    TimeSpan? Lag,
    bool Failed,
    bool MoreDue);

internal sealed record RetentionPass(IReadOnlyList<RetentionTaskPass> Tasks, TimeSpan Duration, bool OutOfBudget)
{
    public int Deleted => Tasks.Sum(task => task.Deleted);
}

// One retention pass: every registered task in turn, each batch in a short transaction of its own,
// and round after round for the tasks whose last batch was full, until no task has a full batch
// left or the pass used its budget, so a backlog drains instead of waiting for the next pass. A
// pass that ran out of budget is followed by one that starts with the task that was cut off, so
// tasks that use the whole budget cannot keep the tasks behind them from ever running.
internal sealed class RetentionRunner
{
    // A batch locks only the rows it deletes, with skip locked, so it never waits for play; the
    // lock timeout guards the rows that a delete reaches through its foreign keys.
    internal static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    private readonly NpgsqlDataSource dataSource;
    private readonly IReadOnlyList<IRetentionTask> tasks;
    private readonly RetentionOptions options;
    private readonly RetentionMetrics metrics;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RetentionRunner> logger;

    // The task the next pass starts with: the first registered one, or the one that the budget
    // of the last pass cut off. The host runs one pass at a time.
    private int firstTask;

    public RetentionRunner(
        NpgsqlDataSource dataSource,
        IEnumerable<IRetentionTask> tasks,
        RetentionOptions options,
        RetentionMetrics metrics,
        TimeProvider timeProvider,
        ILogger<RetentionRunner> logger)
    {
        this.dataSource = dataSource;
        this.tasks = tasks.ToArray();
        this.options = options;
        this.metrics = metrics;
        this.timeProvider = timeProvider;
        this.logger = logger;
        var duplicate = this.tasks
            .GroupBy(task => task.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two retention tasks are named '{duplicate.Key}'; the name is the label of their metrics.");
        }
    }

    public async Task<RetentionPass> RunPassAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var states = tasks.Select(task => new TaskState(task)).ToArray();
        var due = states.Skip(firstTask).Concat(states.Take(firstTask)).ToList();
        firstTask = 0;
        var outOfBudget = false;
        while (due.Count > 0 && !outOfBudget)
        {
            var stillDue = new List<TaskState>();
            foreach (var state in due)
            {
                if (timeProvider.GetElapsedTime(started) >= options.PassBudget)
                {
                    outOfBudget = true;
                    firstTask = Array.IndexOf(states, state);
                    break;
                }

                if (await RunBatchAsync(state, cancellationToken))
                {
                    stillDue.Add(state);
                }
            }

            due = stillDue;
        }

        var pass = new RetentionPass(
            states.Select(state => state.Capture()).ToArray(),
            timeProvider.GetElapsedTime(started),
            outOfBudget);
        foreach (var task in pass.Tasks.Where(task => task.Batches > 0 || task.Failed))
        {
            metrics.RecordPass(task.Task, task.Deleted, task.Duration, task.Lag);
        }

        Log(pass);
        return pass;
    }

    // Runs one batch of the task and says whether it deleted a full batch, so more may be due.
    private async Task<bool> RunBatchAsync(TaskState state, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var window = options.WindowOf(state.Task.Class);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select
                    set_config('statement_timeout', @StatementTimeout, true),
                    set_config('lock_timeout', @LockTimeout, true);
                """,
                new
                {
                    StatementTimeout = Milliseconds(StatementTimeout),
                    LockTimeout = Milliseconds(LockTimeout)
                },
                transaction,
                cancellationToken: cancellationToken));
            var result = await state.Task.DeleteDueAsync(
                new PostgresTransactionScope(connection, transaction),
                new RetentionBatch(window, options.BatchSize),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var full = result.Deleted >= options.BatchSize;
            state.Add(result, window, timeProvider.GetElapsedTime(started), full);
            return full;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState
            is PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.QueryCanceled)
        {
            state.Fail(timeProvider.GetElapsedTime(started));
            logger.LogWarning(
                "Retention task {RetentionTask} gave up a batch with {SqlState}; the next pass tries again.",
                state.Task.Name,
                exception.SqlState);
            return false;
        }
        catch (Exception exception)
        {
            state.Fail(timeProvider.GetElapsedTime(started));
            logger.LogError(exception, "Retention task {RetentionTask} failed.", state.Task.Name);
            return false;
        }
    }

    private void Log(RetentionPass pass)
    {
        if (pass.Deleted > 0)
        {
            logger.LogInformation(
                "Retention pass deleted {DeletedRows} rows in {DurationMilliseconds:0} ms: {RetentionTasks}.",
                pass.Deleted,
                pass.Duration.TotalMilliseconds,
                string.Join(
                    ", ",
                    pass.Tasks
                        .Where(task => task.Deleted > 0)
                        .Select(task => string.Create(CultureInfo.InvariantCulture, $"{task.Task} {task.Deleted}"))));
        }

        if (pass.OutOfBudget)
        {
            logger.LogWarning(
                "Retention pass used its budget of {BudgetSeconds:0.#} s with rows still due in {RetentionTasks}; the next pass goes on.",
                options.PassBudget.TotalSeconds,
                string.Join(", ", pass.Tasks.Where(task => task.MoreDue).Select(task => task.Task)));
        }
    }

    private static string Milliseconds(TimeSpan value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:0}ms");
    }

    private sealed class TaskState(IRetentionTask task)
    {
        private int batches;
        private int deleted;
        private TimeSpan duration;
        private TimeSpan? lag;
        private bool lagMeasured;
        private bool failed;
        private bool finished;

        public IRetentionTask Task { get; } = task;

        // The batches take the oldest due rows first, so the first batch with a row that tells the
        // lag holds the oldest such row, which tells how long rows had been due when the pass
        // began. A task that finished without one had no such row due, so no lag.
        public void Add(RetentionBatchResult result, TimeSpan window, TimeSpan elapsed, bool full)
        {
            if (!lagMeasured && (result.OldestDueAge is not null || !full))
            {
                lagMeasured = true;
                lag = result.OldestDueAge is { } age && age > window ? age - window : TimeSpan.Zero;
            }

            batches++;
            deleted += result.Deleted;
            duration += elapsed;
            finished = !full;
        }

        public void Fail(TimeSpan elapsed)
        {
            failed = true;
            finished = true;
            duration += elapsed;
        }

        public RetentionTaskPass Capture()
        {
            return new RetentionTaskPass(Task.Name, batches, deleted, duration, lag, failed, !finished);
        }
    }
}
