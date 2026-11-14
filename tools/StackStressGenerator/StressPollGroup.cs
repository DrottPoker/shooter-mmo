using System.Collections.Concurrent;
using System.Diagnostics;
using Mmorpg.Server.Common.Timing;

namespace Mmorpg.Tools.StackStressGenerator;

// Polls a share of the bots on its own thread every poll interval, about the frame time of a
// 300 FPS client, so each bot sends a due input in the same poll. Nothing else runs on the
// thread: the coordinator's admissions, reports, and samplers, and the bots' HTTP calls, run
// elsewhere, so they cannot make a poll round late. Everything that touches a bot from outside
// is posted to the thread and runs before the next round. In the steady interval it records,
// for every round, how late the latest bot's poll ran against the schedule.
public sealed class StressPollGroup : IDisposable
{
    private readonly List<IStressBot> bots = [];
    private readonly Dictionary<IStressBot, long> lastPolls = [];
    private readonly ConcurrentQueue<Action<long>> commands = new();
    private readonly TimeSpan interval;
    private readonly Action<double> recordLateness;
    private readonly CancellationToken cancellationToken;
    private readonly Thread thread;
    private volatile bool measuring;
    private volatile bool stopping;
    private volatile Exception? failure;
    private int count;

    public StressPollGroup(
        int index,
        TimeSpan interval,
        Action<double> recordLateness,
        CancellationToken cancellationToken)
    {
        this.interval = interval;
        this.recordLateness = recordLateness;
        this.cancellationToken = cancellationToken;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"StackStressGenerator poll {index + 1}"
        };
        thread.Start();
    }

    public int Count => Volatile.Read(ref count);

    // The exception that stopped the thread, which the coordinator rethrows.
    public Exception? Failure => failure;

    // Starts a bot on the poll thread and polls it from then on.
    public void Add(IStressBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        Interlocked.Increment(ref count);
        commands.Enqueue(_ =>
        {
            bots.Add(bot);
            bot.Start();
        });
    }

    // Runs an action for every bot of the group on the poll thread with the round's timestamp.
    public void ForEachBot(Action<IStressBot, long> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        commands.Enqueue(now =>
        {
            foreach (var bot in bots)
            {
                action(bot, now);
            }
        });
    }

    // Runs an action on the poll thread before the next round, with the round's timestamp, such
    // as a request to one bot of the group.
    public void Post(Action<long> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        commands.Enqueue(action);
    }

    public void BeginSteadyMeasurement()
    {
        commands.Enqueue(_ =>
        {
            lastPolls.Clear();
            measuring = true;
        });
    }

    public void EndSteadyMeasurement()
    {
        commands.Enqueue(_ => measuring = false);
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join();
    }

    private void Run()
    {
        using var timer = new PreciseLoopTimer();
        try
        {
            while (!stopping && !cancellationToken.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                while (commands.TryDequeue(out var command))
                {
                    command(now);
                }

                PollRound(now);
                if (!timer.Wait(interval, cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stopping || cancellationToken.IsCancellationRequested)
        {
            // The run was cancelled or the group stops while a bot waited on something, such as
            // an inventory refresh. The thread ends and the coordinator's cancellation path
            // cleans up. Nothing may escape this method: an unhandled exception on a dedicated
            // thread ends the whole process.
        }
        catch (Exception exception)
        {
            // Also a cancellation that nobody asked for.
            failure = exception;
        }
    }

    private void PollRound(long now)
    {
        var worstLateness = 0d;
        foreach (var bot in bots)
        {
            if (measuring)
            {
                var polled = Stopwatch.GetTimestamp();
                if (lastPolls.TryGetValue(bot, out var previous))
                {
                    worstLateness = Math.Max(
                        worstLateness,
                        Stopwatch.GetElapsedTime(previous, polled).TotalMilliseconds - interval.TotalMilliseconds);
                }

                lastPolls[bot] = polled;
            }

            bot.Poll(now, cancellationToken);
        }

        if (measuring && bots.Count > 0)
        {
            recordLateness(Math.Max(0d, worstLateness));
        }
    }
}
