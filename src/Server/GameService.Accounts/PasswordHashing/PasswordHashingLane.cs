using System.Diagnostics;
using System.Globalization;

namespace Mmorpg.GameService.Accounts.PasswordHashing;

// Hashes and verifies passwords. Only the lane calls it.
internal interface IPasswordHashAlgorithm
{
    // A real hash of a random password, which an unknown login is verified against so that it
    // costs the same as a wrong password and timing does not reveal whether an account exists.
    string UnknownAccountHash { get; }

    string Hash(string password);

    bool Verify(string password, string passwordHash);
}

internal sealed class BCryptPasswordHashAlgorithm : IPasswordHashAlgorithm
{
    // About a quarter of a second of one core per hash on the reference machine.
    public const int WorkFactor = 12;

    // Made once per process.
    private static readonly Lazy<string> Unknown = new(
        () => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), WorkFactor),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public string UnknownAccountHash => Unknown.Value;

    public string Hash(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string passwordHash)
    {
        return BCrypt.Net.BCrypt.Verify(password, passwordHash);
    }
}

// The value of a hash or a verify, or the refusal with how long the caller should wait.
internal readonly record struct PasswordHashingResult<T>(T Value, TimeSpan? RetryAfter)
{
    public bool Refused => RetryAfter is not null;

    public static PasswordHashingResult<T> Done(T value)
    {
        return new PasswordHashingResult<T>(value, null);
    }

    public static PasswordHashingResult<T> Refuse(TimeSpan retryAfter)
    {
        return new PasswordHashingResult<T>(default!, retryAfter);
    }
}

// Runs every password hash of GameService on its own threads, at most MaxConcurrency at once
// and below normal priority, so a registration or login burst takes neither the thread pool
// nor every core. A request waits in a first-in first-out queue. A request that would wait
// longer than MaxWait, by the measured time of a hash, is refused at once, so a refused request
// never waited; an admitted one keeps its place until it is hashed or its caller gives up.
internal sealed class PasswordHashingLane : IDisposable
{
    public const string HashOperation = "hash";
    public const string VerifyOperation = "verify";

    // What a hash costs until the first one is measured; work factor 12 on one core.
    internal static readonly TimeSpan InitialHashTimeEstimate = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan MinimumHashTimeEstimate = TimeSpan.FromMilliseconds(1);
    private const double EstimateWeight = 0.2;
    private const long WarningIntervalMilliseconds = 30_000;

    private readonly object gate = new();
    private readonly LinkedList<WorkItem> queue = new();
    private readonly PasswordHashingOptions options;
    private readonly IPasswordHashAlgorithm algorithm;
    private readonly PasswordHashingMetrics metrics;
    private readonly ILogger<PasswordHashingLane> logger;
    private double hashTicksEstimate;
    private int hashing;
    private bool threadsStarted;
    private bool disposed;
    private long lastWarningAt = long.MinValue;

    public PasswordHashingLane(
        PasswordHashingOptions options,
        IPasswordHashAlgorithm algorithm,
        PasswordHashingMetrics metrics,
        ILogger<PasswordHashingLane> logger)
        : this(options, algorithm, metrics, logger, InitialHashTimeEstimate)
    {
    }

    internal PasswordHashingLane(
        PasswordHashingOptions options,
        IPasswordHashAlgorithm algorithm,
        PasswordHashingMetrics metrics,
        ILogger<PasswordHashingLane> logger,
        TimeSpan initialHashTime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrency, 1);

        this.options = options;
        this.algorithm = algorithm;
        this.metrics = metrics;
        this.logger = logger;
        hashTicksEstimate = Math.Max(initialHashTime.Ticks, MinimumHashTimeEstimate.Ticks);
    }

    // Hashes a new password for storage.
    public Task<PasswordHashingResult<string>> HashAsync(
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        return SubmitAsync(HashOperation, () => algorithm.Hash(password), cancellationToken);
    }

    // Checks a password against the stored hash, or against the unknown-account hash when the
    // account does not exist, at the same cost and through the same queue either way.
    public Task<PasswordHashingResult<bool>> VerifyAsync(
        string password,
        string? passwordHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        return SubmitAsync(
            VerifyOperation,
            () => algorithm.Verify(password, passwordHash ?? algorithm.UnknownAccountHash),
            cancellationToken);
    }

    // Queued requests are refused, the threads end after the hash they run, and nothing waits.
    public void Dispose()
    {
        WorkItem[] refused;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            refused = [.. queue];
            foreach (var item in refused)
            {
                item.Node = null;
            }

            queue.Clear();
            PublishLevels();
            Monitor.PulseAll(gate);
        }

        foreach (var item in refused)
        {
            item.Refuse(options.MaxWait);
        }
    }

    private async Task<PasswordHashingResult<T>> SubmitAsync<T>(
        string operation,
        Func<T> run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<PasswordHashingResult<T>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(
            () =>
            {
                var result = PasswordHashingResult<T>.Done(run());
                return () => completion.TrySetResult(result);
            },
            exception => completion.TrySetException(exception),
            retryAfter => completion.TrySetResult(PasswordHashingResult<T>.Refuse(retryAfter)),
            Stopwatch.GetTimestamp());

        Refusal? refusal = null;
        lock (gate)
        {
            if (disposed)
            {
                refusal = new Refusal(options.MaxWait, queue.Count, hashing, Estimate());
            }
            else
            {
                var free = Math.Max(0, options.MaxConcurrency - hashing);
                if (queue.Count >= free + (long)options.MaxConcurrency * Rounds())
                {
                    refusal = new Refusal(RetryAfter(free), queue.Count, hashing, Estimate());
                }
                else
                {
                    StartThreads();
                    item.Node = queue.AddLast(item);
                    PublishLevels();
                    Monitor.Pulse(gate);
                }
            }
        }

        if (refusal is { } refused)
        {
            metrics.RecordRefusal(operation);
            WarnRefusal(operation, refused);
            return PasswordHashingResult<T>.Refuse(refused.RetryAfter);
        }

        // A caller that gives up leaves the queue at once; a hash in flight runs to its end.
        await using var registration = cancellationToken.Register(() =>
        {
            lock (gate)
            {
                if (item.Node is not null)
                {
                    queue.Remove(item.Node);
                    item.Node = null;
                    PublishLevels();
                }
            }

            completion.TrySetCanceled(cancellationToken);
        });
        return await completion.Task;
    }

    // The threads start with the first request, so a lane that never hashes holds none. The
    // first thread makes the unknown-account hash before it takes work. Called under the gate.
    private void StartThreads()
    {
        if (threadsStarted)
        {
            return;
        }

        threadsStarted = true;
        for (var index = 0; index < options.MaxConcurrency; index++)
        {
            var thread = new Thread(Work)
            {
                IsBackground = true,
                Name = "password-hashing-" + index.ToString(CultureInfo.InvariantCulture),
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start(index == 0);
        }
    }

    private void Work(object? state)
    {
        if (state is true)
        {
            _ = algorithm.UnknownAccountHash;
        }

        while (true)
        {
            WorkItem item;
            lock (gate)
            {
                while (queue.Count == 0 && !disposed)
                {
                    Monitor.Wait(gate);
                }

                if (disposed)
                {
                    return;
                }

                item = queue.First!.Value;
                queue.RemoveFirst();
                item.Node = null;
                hashing++;
                PublishLevels();
            }

            var started = Stopwatch.GetTimestamp();
            metrics.RecordWait(Stopwatch.GetElapsedTime(item.EnqueuedAt, started));
            Action publish;
            try
            {
                publish = item.Run();
            }
            catch (Exception exception)
            {
                publish = () => item.Fail(exception);
            }

            var duration = Stopwatch.GetElapsedTime(started);
            metrics.RecordHash(duration);
            lock (gate)
            {
                hashing--;
                hashTicksEstimate = Math.Max(
                    MinimumHashTimeEstimate.Ticks,
                    hashTicksEstimate + EstimateWeight * (duration.Ticks - hashTicksEstimate));
                PublishLevels();
            }

            // The caller gets its answer only once the thread counts as free again, so a request
            // that follows the answer at once is not refused for a thread that just finished.
            publish();
        }
    }

    // How many hashes one lane thread finishes within the longest wait, by the estimate.
    private long Rounds()
    {
        return (long)Math.Floor(options.MaxWait.Ticks / hashTicksEstimate);
    }

    // The time the queue ahead needs to drain, in whole seconds, at least one and at most the
    // longest wait, so a caller that comes back then finds room.
    private TimeSpan RetryAfter(int free)
    {
        var rounds = Math.Max(1, (long)Math.Ceiling((queue.Count - free + 1) / (double)options.MaxConcurrency));
        var seconds = Math.Ceiling(rounds * hashTicksEstimate / TimeSpan.TicksPerSecond);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1d, Math.Max(1d, Math.Ceiling(options.MaxWait.TotalSeconds))));
    }

    private TimeSpan Estimate()
    {
        return TimeSpan.FromTicks((long)hashTicksEstimate);
    }

    private void PublishLevels()
    {
        metrics.SetLevels(hashing, queue.Count);
    }

    private void WarnRefusal(string operation, Refusal refusal)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastWarningAt);
        if ((previous != long.MinValue && now - previous < WarningIntervalMilliseconds)
            || Interlocked.CompareExchange(ref lastWarningAt, now, previous) != previous)
        {
            return;
        }

        logger.LogWarning(
            "[AUTH] Password hashing refused a {Operation} with auth_busy: {Waiting} requests wait and {Hashing} hash, a hash takes about {HashMilliseconds:0} ms, and the wait would exceed {MaxWaitSeconds:0} s. Retry after {RetryAfterSeconds:0} s. Repeated warnings are throttled.",
            operation,
            refusal.Waiting,
            refusal.Hashing,
            refusal.Estimate.TotalMilliseconds,
            options.MaxWait.TotalSeconds,
            refusal.RetryAfter.TotalSeconds);
    }

    private sealed class WorkItem(
        Func<Action> run,
        Action<Exception> fail,
        Action<TimeSpan> refuse,
        long enqueuedAt)
    {
        // Does the work and returns what hands its result to the caller.
        public Func<Action> Run { get; } = run;

        public Action<Exception> Fail { get; } = fail;

        public Action<TimeSpan> Refuse { get; } = refuse;

        public long EnqueuedAt { get; } = enqueuedAt;

        // The item's place in the queue while it waits; only touched under the lane's gate.
        public LinkedListNode<WorkItem>? Node { get; set; }
    }

    private readonly record struct Refusal(TimeSpan RetryAfter, int Waiting, int Hashing, TimeSpan Estimate);
}
