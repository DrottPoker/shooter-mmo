using Microsoft.Extensions.Logging;

namespace Mmorpg.SimulationWorker.GameServiceApi;

// The one place that knows whether GameService answers. The registry heartbeat is the probe: a
// success makes GameService available, and a timeout, a lost connection, or a server error makes
// it unavailable. Any other GameService call that fails that way while GameService still counts
// as available asks for the next probe at once. While GameService is unavailable, the realtime
// loop refuses the durable actions that players ask for, and the work that must complete waits
// for it to return instead of spending timeouts. The loop reads IsAvailable, Version, and
// AvailableTime without a lock or an allocation; the changes take a lock. Thread-safe.
public sealed class GameServiceAvailability
{
    // What a refused durable action tells the client.
    public const string UnavailableCode = "game_service_unavailable";
    public const string UnavailableMessage = "GameService does not answer, so items, trade, and quests are paused.";

    // What the stop after a whole outage tolerance tells the client.
    public const string OutageStopCode = "game_service_outage";
    public const string OutageStopMessage = "GameService did not answer for the outage tolerance, so the shard shut down.";

    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly GameServiceAvailabilityMetrics? metrics;
    private readonly object sync = new();
    private volatile AvailabilityState state;
    private long lastHeartbeatTimestamp;
    private TaskCompletionSource probeRequest = NewSignal();
    private TaskCompletionSource<bool> returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool waitsEnded;

    public GameServiceAvailability(
        TimeProvider timeProvider,
        ILogger<GameServiceAvailability> logger,
        GameServiceAvailabilityMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.metrics = metrics;
        state = new AvailabilityState(true, 0, timeProvider.GetTimestamp(), TimeSpan.Zero);
        returned.SetResult(true);
        metrics?.Observe(this);
    }

    // GameService is not known to be down; it starts so, before the first heartbeat.
    public bool IsAvailable => state.IsAvailable;

    // Raised by every change of the state, so the loop sees a change without comparing states.
    public int Version => state.Version;

    // How long GameService has counted as available since the worker started. It stands still
    // while GameService is unavailable, so retry horizons count only the time it answered.
    public TimeSpan AvailableTime
    {
        get
        {
            var current = state;
            return current.IsAvailable
                ? current.AvailableBefore + timeProvider.GetElapsedTime(current.SinceTimestamp)
                : current.AvailableBefore;
        }
    }

    // The time since the last successful registry heartbeat, or null before the first one.
    public TimeSpan? TimeSinceLastHeartbeat
    {
        get
        {
            var timestamp = Interlocked.Read(ref lastHeartbeatTimestamp);
            return timestamp == 0 ? null : timeProvider.GetElapsedTime(timestamp);
        }
    }

    // A registry heartbeat succeeded. A return logs how long GameService was away.
    public void HeartbeatSucceeded()
    {
        Interlocked.Exchange(ref lastHeartbeatTimestamp, Math.Max(1, timeProvider.GetTimestamp()));
        TaskCompletionSource<bool>? waiters = null;
        TimeSpan outage;
        lock (sync)
        {
            var current = state;
            if (current.IsAvailable)
            {
                return;
            }

            var now = timeProvider.GetTimestamp();
            outage = timeProvider.GetElapsedTime(current.SinceTimestamp, now);
            state = new AvailabilityState(true, current.Version + 1, now, current.AvailableBefore);
            waiters = returned;
        }

        waiters.TrySetResult(true);
        metrics?.RecordChange(available: true);
        logger.LogInformation(
            "GameService answers the registry heartbeat again after {Outage}; durable actions resume.",
            outage);
    }

    // A registry heartbeat timed out, could not reach GameService, or got a server error.
    public void HeartbeatFailed(string code)
    {
        lock (sync)
        {
            var current = state;
            if (!current.IsAvailable)
            {
                return;
            }

            var now = timeProvider.GetTimestamp();
            state = new AvailabilityState(
                false,
                current.Version + 1,
                now,
                current.AvailableBefore + timeProvider.GetElapsedTime(current.SinceTimestamp, now));
            if (!waitsEnded)
            {
                returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        metrics?.RecordChange(available: false);
        logger.LogWarning(
            "GameService does not answer the registry heartbeat ({Code}); items, trade, and quests are paused until it answers again.",
            code);
    }

    // A GameService call other than the registry heartbeat failed for a passing reason. While
    // GameService still counts as available, the heartbeat goes at once to find out.
    public void RequestProbe()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (sync)
        {
            probeRequest.TrySetResult();
        }
    }

    // Completes when a probe was asked for since the last heartbeat started.
    public Task WaitForProbeRequestAsync()
    {
        lock (sync)
        {
            return probeRequest.Task;
        }
    }

    // A heartbeat starts, which answers every probe request so far.
    public void ProbeStarted()
    {
        lock (sync)
        {
            if (probeRequest.Task.IsCompleted)
            {
                probeRequest = NewSignal();
            }
        }
    }

    // True at once while GameService is available, else once it is again; false when the worker
    // stops first, so nothing that waits keeps the shutdown waiting.
    public Task<bool> WaitUntilAvailableAsync(CancellationToken cancellationToken)
    {
        Task<bool> waiting;
        lock (sync)
        {
            if (state.IsAvailable)
            {
                return Task.FromResult(true);
            }

            if (waitsEnded)
            {
                return Task.FromResult(false);
            }

            waiting = returned.Task;
        }

        return waiting.WaitAsync(cancellationToken);
    }

    // The worker stops: everything that waits for GameService gives up, now and later.
    public void EndWaits()
    {
        TaskCompletionSource<bool> waiters;
        lock (sync)
        {
            waitsEnded = true;
            waiters = returned;
        }

        waiters.TrySetResult(false);
    }

    // A client action that GameService must commit was refused while it did not answer.
    public void RecordRefusedIntent(string kind)
    {
        metrics?.RecordRefusedIntent(kind);
    }

    // An operation whose outcome stayed uncertain past the retry horizon was given up.
    public void RecordAbandonedOperation(string kind)
    {
        metrics?.RecordAbandonedOperation(kind);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // SinceTimestamp is when the state began; AvailableBefore the available time before it.
    private sealed record AvailabilityState(
        bool IsAvailable,
        int Version,
        long SinceTimestamp,
        TimeSpan AvailableBefore);
}
