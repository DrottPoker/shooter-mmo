namespace Mmorpg.SimulationWorker.Realtime;

// When the realtime loop last completed a tick, for the worker's health file. The loop writes one
// timestamp per completed tick with a single volatile write, no lock and no allocation, and the
// health file service reads it from its own thread. A loop that hangs, in a deadlock or an endless
// loop inside a handler, stops writing it while everything else of the worker, its registration
// and its UDP port, still looks alive.
//
// The timestamps are those of TimeProvider.GetTimestamp of the clock that reads them; the loop uses
// Stopwatch.GetTimestamp, which is the one of TimeProvider.System.
public sealed class RealtimeLoopLiveness
{
    // Zero means that no tick completed yet.
    private long lastTickCompleted;

    public bool HasTicked => Volatile.Read(ref lastTickCompleted) != 0;

    // Called by the loop at the end of every tick.
    public void TickCompleted(long timestamp)
    {
        Volatile.Write(ref lastTickCompleted, timestamp);
    }

    // How long ago the last tick completed by the clock, or null when none did yet.
    public TimeSpan? SilenceFor(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var last = Volatile.Read(ref lastTickCompleted);
        return last == 0 ? null : timeProvider.GetElapsedTime(last, timeProvider.GetTimestamp());
    }
}
