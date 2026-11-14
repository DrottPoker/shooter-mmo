using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// The poll threads that share the bots of a run: nothing but the bots runs on them, and
// everything that touches a bot from outside is posted to them.
public sealed class StressPollGroupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void ABotStartsAndIsPolledOnThePollThreadAndPostedActionsRunThere()
    {
        var lateness = new List<double>();
        using var group = new StressPollGroup(0, TimeSpan.FromMilliseconds(3), value =>
        {
            lock (lateness)
            {
                lateness.Add(value);
            }
        }, CancellationToken.None);
        var first = new RecordingBot();
        var second = new RecordingBot();

        group.Add(first);
        group.Add(second);
        Assert.True(first.Polled.Wait(Wait, TestContext.Current.CancellationToken));
        Assert.True(second.Polled.Wait(Wait, TestContext.Current.CancellationToken));
        using var visited = new CountdownEvent(2);
        var threads = new List<string?>();
        group.ForEachBot((bot, _) =>
        {
            threads.Add(Thread.CurrentThread.Name);
            visited.Signal();
        });
        Assert.True(visited.Wait(Wait, TestContext.Current.CancellationToken));
        group.BeginSteadyMeasurement();
        first.ResetPolls();
        Assert.True(first.WaitForPolls(5, Wait));
        group.EndSteadyMeasurement();

        Assert.Equal(2, group.Count);
        Assert.Equal("StackStressGenerator poll 1", first.StartThread);
        Assert.Equal("StackStressGenerator poll 1", first.PollThread);
        Assert.All(threads, name => Assert.Equal("StackStressGenerator poll 1", name));
        lock (lateness)
        {
            Assert.NotEmpty(lateness);
            Assert.All(lateness, value => Assert.True(value >= 0d));
        }

        Assert.Null(group.Failure);
    }

    [Fact]
    public void ABotThatThrowsStopsTheThreadAndTheGroupReportsIt()
    {
        using var group = new StressPollGroup(1, TimeSpan.FromMilliseconds(3), _ => { }, CancellationToken.None);
        var bot = new RecordingBot { Throw = true };

        group.Add(bot);
        var deadline = DateTime.UtcNow + Wait;
        while (group.Failure is null && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.IsType<InvalidOperationException>(group.Failure);
    }

    // A bot can throw the cancellation of the run from inside a poll, for example while it
    // completes an inventory refresh. On a dedicated thread an exception that escapes ends the
    // process, so the thread ends quietly and reports no failure.
    [Fact]
    public void ACancelledRunEndsThePollThreadWithoutAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var group = new StressPollGroup(2, TimeSpan.FromMilliseconds(3), _ => { }, cancellation.Token);
        var bot = new RecordingBot { ThrowCancellationWhenCancelled = true };
        group.Add(bot);
        Assert.True(bot.Polled.Wait(Wait, TestContext.Current.CancellationToken));

        cancellation.Cancel();
        group.Dispose();

        Assert.Null(group.Failure);
    }

    [Fact]
    public void ACancellationThatNobodyAskedForIsReportedAsAFailure()
    {
        using var group = new StressPollGroup(3, TimeSpan.FromMilliseconds(3), _ => { }, CancellationToken.None);
        var bot = new RecordingBot { ThrowCancellation = true };

        group.Add(bot);
        var deadline = DateTime.UtcNow + Wait;
        while (group.Failure is null && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.IsType<OperationCanceledException>(group.Failure);
    }

    private sealed class RecordingBot : IStressBot
    {
        private int polls;

        public ManualResetEventSlim Polled { get; } = new();

        public bool Throw { get; init; }

        public bool ThrowCancellation { get; init; }

        public bool ThrowCancellationWhenCancelled { get; init; }

        public string? StartThread { get; private set; }

        public string? PollThread { get; private set; }

        public StressBotState State => StressBotState.Joined;

        public StressSessionHolding SessionHolding => StressSessionHolding.Holds;

        public bool IsGameplayIdle => true;

        public void Start()
        {
            StartThread = Thread.CurrentThread.Name;
        }

        public void Poll(long nowTimestamp, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("broken bot");
            }

            if (ThrowCancellation)
            {
                throw new OperationCanceledException("unexpected");
            }

            PollThread = Thread.CurrentThread.Name;
            Interlocked.Increment(ref polls);
            Polled.Set();
            if (ThrowCancellationWhenCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void ResetPolls()
        {
            Interlocked.Exchange(ref polls, 0);
        }

        public bool WaitForPolls(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref polls) < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }

                Thread.Yield();
            }

            return true;
        }

        public void BeginLeave(long nowTimestamp)
        {
        }

        public void BeginGameplayDrain()
        {
        }

        public void BeginRemoteCadenceWindow(long nowTimestamp)
        {
        }

        public void EndRemoteCadenceWindow(long nowTimestamp)
        {
        }

        public StressBotSnapshot Capture()
        {
            throw new NotSupportedException();
        }

        public StressBotProgress CaptureProgress()
        {
            return new StressBotProgress(State, 0, 0);
        }

        public void Dispose()
        {
            Polled.Dispose();
        }
    }
}
