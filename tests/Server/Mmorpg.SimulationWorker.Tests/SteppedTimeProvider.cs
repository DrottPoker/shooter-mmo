namespace Mmorpg.SimulationWorker.Tests;

// A clock that moves only when a test advances it. Its timers, which Task.Delay and
// PeriodicTimer create on it, fire on the advancing thread in due order, so background loops
// that wait on it run step by step in fake time. Thread-safe.
internal sealed class SteppedTimeProvider : TimeProvider
{
    // Timestamps start above zero, so no code mistakes the start for an unset timestamp.
    private const long TimestampBase = TimeSpan.TicksPerDay;

    private readonly object sync = new();
    private readonly List<SteppedTimer> timers = [];
    private readonly long startUtcTicks;
    private long elapsedTicks;

    public SteppedTimeProvider(DateTimeOffset start)
    {
        startUtcTicks = start.UtcTicks;
    }

    // The time advanced since the start.
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks));

    // Timers that wait to fire.
    public int PendingTimers
    {
        get
        {
            lock (sync)
            {
                return timers.Count(timer => timer.DueTicks is not null);
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        return new DateTimeOffset(startUtcTicks + Interlocked.Read(ref elapsedTicks), TimeSpan.Zero);
    }

    public override long GetTimestamp()
    {
        return TimestampBase + Interlocked.Read(ref elapsedTicks);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new SteppedTimer(this, callback, state);
        lock (sync)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    // Moves the clock forward and fires every timer that comes due on the way, in due order, each
    // at its own time.
    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        var target = Interlocked.Read(ref elapsedTicks) + duration.Ticks;
        while (true)
        {
            SteppedTimer? due = null;
            lock (sync)
            {
                foreach (var timer in timers)
                {
                    if (timer.DueTicks is { } ticks
                        && ticks <= target
                        && (due is null || ticks < due.DueTicks!.Value))
                    {
                        due = timer;
                    }
                }

                if (due is null)
                {
                    Interlocked.Exchange(ref elapsedTicks, target);
                    return;
                }

                Interlocked.Exchange(ref elapsedTicks, Math.Max(Interlocked.Read(ref elapsedTicks), due.DueTicks!.Value));
                due.Fired();
            }

            due.Invoke();
        }
    }

    private void Remove(SteppedTimer timer)
    {
        lock (sync)
        {
            timers.Remove(timer);
        }
    }

    private sealed class SteppedTimer(SteppedTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long periodTicks;

        // When the timer fires next, or null while it waits for nothing.
        public long? DueTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                periodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
                DueTicks = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : Interlocked.Read(ref owner.elapsedTicks) + Math.Max(0, dueTime.Ticks);
            }

            return true;
        }

        // Called under the owner's lock when the timer fires: a periodic timer comes due again.
        public void Fired()
        {
            DueTicks = periodTicks > 0 ? DueTicks + periodTicks : null;
        }

        public void Invoke()
        {
            callback(state);
        }

        public void Dispose()
        {
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
