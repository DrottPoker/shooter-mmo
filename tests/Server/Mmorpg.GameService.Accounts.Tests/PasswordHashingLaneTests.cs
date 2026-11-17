using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Accounts.PasswordHashing;

namespace Mmorpg.GameService.Accounts.Tests;

public sealed class PasswordHashingLaneTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // One lane thread and a hash estimated at a quarter second fit four hashes into a wait of
    // one second: one runs, four wait, and the sixth is refused at once, without waiting.
    [Fact]
    public async Task TheQueueHoldsWhatFitsTheLongestWaitAndRefusesTheRestAtOnce()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        using var lane = CreateLane(algorithm, metrics, 1, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));
        var cancellationToken = TestContext.Current.CancellationToken;

        var running = lane.HashAsync("wait-0", cancellationToken);
        await WaitUntilAsync(() => metrics.Capture().InFlight == 1);
        var queued = Enumerable.Range(1, 4)
            .Select(index => lane.HashAsync("wait-" + index, cancellationToken))
            .ToArray();
        var started = Stopwatch.GetTimestamp();
        var refused = await lane.VerifyAsync("wait-5", "stored", cancellationToken);

        Assert.True(refused.Refused);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1), "A refusal does not wait.");
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);
        Assert.Equal(new PasswordHashingMetricsSnapshot(1, 4, 0, 1), metrics.Capture());

        algorithm.Release();
        var results = await Task.WhenAll(queued.Prepend(running));
        Assert.All(results, result => Assert.False(result.Refused));
        Assert.Equal(["hash:wait-0", "hash:wait-1", "hash:wait-2", "hash:wait-3", "hash:wait-4"], algorithm.Calls);
        await WaitUntilAsync(() => metrics.Capture() is { InFlight: 0, Queued: 0, Hashes: 5 });
    }

    // Hashes run on the lane's own threads, never on a thread of the pool that serves requests,
    // and never more at once than the lane allows.
    [Fact]
    public async Task HashesRunOnTheLanesThreadsAndNoMoreAtOnceThanItsConcurrency()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        using var lane = CreateLane(algorithm, metrics, 2, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));
        var cancellationToken = TestContext.Current.CancellationToken;

        var hashes = Enumerable.Range(0, 6)
            .Select(index => lane.HashAsync("wait-" + index, cancellationToken))
            .ToArray();
        await WaitUntilAsync(() => metrics.Capture() is { InFlight: 2, Queued: 4 });
        algorithm.Release();
        await Task.WhenAll(hashes);

        Assert.Equal(2, algorithm.MostAtOnce);
        Assert.All(algorithm.Threads, thread => Assert.StartsWith("password-hashing-", thread, StringComparison.Ordinal));
        Assert.False(algorithm.RanOnThreadPool);
    }

    // A caller that gives up leaves the queue, and its password is never hashed.
    [Fact]
    public async Task ACancelledRequestLeavesTheQueue()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        using var lane = CreateLane(algorithm, metrics, 1, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));
        using var cancellation = new CancellationTokenSource();

        var running = lane.HashAsync("wait-running", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => metrics.Capture().InFlight == 1);
        var waiting = lane.HashAsync("wait-cancelled", cancellation.Token);
        Assert.Equal(1, metrics.Capture().Queued);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, metrics.Capture().Queued);
        algorithm.Release();
        Assert.False((await running).Refused);
        Assert.Equal(["hash:wait-running"], algorithm.Calls);
    }

    // Shutdown refuses what waits and returns at once; the hash in flight still finishes.
    [Fact]
    public async Task DisposeRefusesQueuedWorkWithoutWaitingForIt()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        var lane = CreateLane(algorithm, metrics, 1, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));
        var cancellationToken = TestContext.Current.CancellationToken;
        var running = lane.HashAsync("wait-running", cancellationToken);
        await WaitUntilAsync(() => metrics.Capture().InFlight == 1);
        var waiting = lane.HashAsync("wait-queued", cancellationToken);

        var started = Stopwatch.GetTimestamp();
        lane.Dispose();

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1), "Dispose does not wait for queued work.");
        var refused = await waiting;
        Assert.True(refused.Refused);
        Assert.Equal(TimeSpan.FromSeconds(10), refused.RetryAfter);
        Assert.True((await lane.HashAsync("after", cancellationToken)).Refused);
        algorithm.Release();
        Assert.False((await running).Refused);
    }

    // A caller has its answer only once the lane's thread counts as free again: with a hash that
    // fits no wait, a request that followed an answer at once was refused while the thread that
    // answered still counted as busy. The lane records a hash's duration before it frees the
    // thread, so a listener that holds that recording holds the thread there.
    [Fact]
    public async Task ACallerHasItsAnswerOnlyOnceTheThreadIsFree()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        using var lane = CreateLane(algorithm, metrics, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var cancellationToken = TestContext.Current.CancellationToken;
        using var recorded = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PasswordHashingMetrics.MeterName && instrument.Name.Contains("hash", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            if (instrument.Name.Contains("duration", StringComparison.Ordinal) && !instrument.Name.Contains("wait", StringComparison.Ordinal))
            {
                recorded.Release();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        listener.Start();

        var first = lane.VerifyAsync("password", "stored", cancellationToken);
        Assert.True(await recorded.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken), "The lane recorded no hash.");
        await Task.Delay(50, cancellationToken);
        Assert.False(first.IsCompleted, "The caller had its answer while the thread still counted as busy.");
        release.Set();
        Assert.False((await first).Refused);
        Assert.False((await lane.VerifyAsync("password", "stored", cancellationToken)).Refused);
    }

    // An unknown account is verified against the algorithm's unknown-account hash, and it is
    // refused exactly like a known account when the lane is full.
    [Fact]
    public async Task AnUnknownAccountCostsAndIsRefusedLikeAKnownOne()
    {
        using var algorithm = new GatedAlgorithm();
        using var metrics = new PasswordHashingMetrics();
        using var lane = CreateLane(algorithm, metrics, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var cancellationToken = TestContext.Current.CancellationToken;

        var unknown = await lane.VerifyAsync("password", null, cancellationToken);
        var known = await lane.VerifyAsync("password", "stored", cancellationToken);
        Assert.False(unknown.Refused);
        Assert.False(unknown.Value);
        Assert.False(known.Refused);
        Assert.Equal(["verify:password:" + algorithm.UnknownAccountHash, "verify:password:stored"], algorithm.Calls);

        // A hash estimated at two seconds fits no wait of one second, so with the one thread busy
        // every further request is refused.
        var running = lane.HashAsync("wait-running", cancellationToken);
        await WaitUntilAsync(() => metrics.Capture().InFlight == 1);
        var unknownRefused = await lane.VerifyAsync("password", null, cancellationToken);
        var knownRefused = await lane.VerifyAsync("password", "stored", cancellationToken);
        Assert.True(unknownRefused.Refused);
        Assert.True(knownRefused.Refused);
        Assert.Equal(knownRefused.RetryAfter, unknownRefused.RetryAfter);
        algorithm.Release();
        await running;
    }

    [Fact]
    public void TheSettingsDefaultToHalfTheProcessorsAndTenSeconds()
    {
        var errors = new List<string>();
        var defaults = PasswordHashingOptions.Read(new ConfigurationBuilder().Build(), errors);

        Assert.Empty(errors);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), defaults.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.MaxWait);

        var configured = PasswordHashingOptions.Read(
            Configuration(("Auth:PasswordHashing:MaxConcurrency", "3"), ("Auth:PasswordHashing:MaxWaitSeconds", "4")),
            errors);
        Assert.Empty(errors);
        Assert.Equal(new PasswordHashingOptions(3, TimeSpan.FromSeconds(4)), configured);
    }

    [Theory]
    [InlineData("Auth:PasswordHashing:MaxConcurrency", "0", "from 1 to 256")]
    [InlineData("Auth:PasswordHashing:MaxConcurrency", "257", "from 1 to 256")]
    [InlineData("Auth:PasswordHashing:MaxWaitSeconds", "0", "from 1 to 10")]
    [InlineData("Auth:PasswordHashing:MaxWaitSeconds", "ten", "from 1 to 10")]
    // More than a client waits for a queued login.
    [InlineData("Auth:PasswordHashing:MaxWaitSeconds", "11", "from 1 to 10")]
    public void TheSettingsRejectValuesOutOfRange(string key, string value, string expected)
    {
        var errors = new List<string>();

        PasswordHashingOptions.Read(Configuration((key, value)), errors);

        var error = Assert.Single(errors);
        Assert.Contains(key, error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    internal static PasswordHashingLane CreateLane(
        IPasswordHashAlgorithm algorithm,
        PasswordHashingMetrics metrics,
        int concurrency,
        TimeSpan maxWait,
        TimeSpan initialHashTime)
    {
        return new PasswordHashingLane(
            new PasswordHashingOptions(concurrency, maxWait),
            algorithm,
            metrics,
            NullLogger<PasswordHashingLane>.Instance,
            initialHashTime);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > Patience)
            {
                throw new TimeoutException("The lane did not reach the expected state in time.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    // Hashes instantly, except a password that starts with "wait", which holds until Release.
    internal sealed class GatedAlgorithm : IPasswordHashAlgorithm, IDisposable
    {
        private readonly ManualResetEventSlim gate = new(false);
        private readonly ConcurrentQueue<string> calls = new();
        private readonly ConcurrentBag<string> threads = [];
        private int running;
        private int mostAtOnce;
        private volatile bool ranOnThreadPool;

        public string UnknownAccountHash => "unknown-account-hash";

        public IReadOnlyList<string> Calls => [.. calls];

        public IReadOnlyCollection<string> Threads => [.. threads];

        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        public bool RanOnThreadPool => ranOnThreadPool;

        public string Hash(string password)
        {
            Run("hash:" + password, password);
            return "hashed-" + password;
        }

        public bool Verify(string password, string passwordHash)
        {
            Run("verify:" + password + ":" + passwordHash, password);
            return passwordHash == "hashed-" + password;
        }

        public void Release()
        {
            gate.Set();
        }

        public void Dispose()
        {
            gate.Set();
            gate.Dispose();
        }

        private void Run(string call, string password)
        {
            calls.Enqueue(call);
            threads.Add(Thread.CurrentThread.Name ?? string.Empty);
            ranOnThreadPool |= Thread.CurrentThread.IsThreadPoolThread;
            var now = Interlocked.Increment(ref running);
            int most;
            while (now > (most = Volatile.Read(ref mostAtOnce))
                && Interlocked.CompareExchange(ref mostAtOnce, now, most) != most)
            {
            }

            try
            {
                if (password.StartsWith("wait", StringComparison.Ordinal))
                {
                    gate.Wait(Patience);
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }
    }
}
