using Mmorpg.Client.Networking;

namespace Mmorpg.Client.Combat
{
    // The client's best estimate of the worker's current tick: the tick of the newest owner
    // snapshot plus the time since it left the worker, which is the time since it arrived
    // plus the one-way latency. Presentation only, such as a respawn countdown or an arrow
    // that lands at its impact tick; nothing here feeds prediction or combat decisions.
    public readonly struct CombatServerClock
    {
        public CombatServerClock(uint latestServerTick, float secondsSinceLatestTick, int tickRateHz)
        {
            LatestServerTick = latestServerTick;
            SecondsSinceLatestTick = secondsSinceLatestTick < 0f ? 0f : secondsSinceLatestTick;
            TickRateHz = tickRateHz;
        }

        public uint LatestServerTick { get; }

        public float SecondsSinceLatestTick { get; }

        public int TickRateHz { get; }

        // False before the first owner snapshot of the session.
        public bool IsKnown
        {
            get { return LatestServerTick != 0 && TickRateHz > 0; }
        }

        // The clock of the joined session, from its diagnostics and the tick rate of its
        // movement session; unknown outside a session or before its first owner snapshot.
        public static CombatServerClock FromDiagnostics(
            IRealtimeDiagnostics diagnostics,
            NetworkMovementSession session)
        {
            if (diagnostics == null || session == null || diagnostics.LatestSnapshotAgeSeconds < 0f)
            {
                return default(CombatServerClock);
            }

            var oneWayLatencySeconds = diagnostics.RoundTripTimeMilliseconds * 0.0005f;
            return new CombatServerClock(
                diagnostics.LatestServerTick,
                diagnostics.LatestSnapshotAgeSeconds + oneWayLatencySeconds,
                session.Settings.TickRateHz);
        }

        // Seconds until the worker reaches the tick, negative once it has passed. Ticks
        // wrap, so the difference is taken in tick space.
        public float SecondsUntil(uint tick)
        {
            if (!IsKnown)
            {
                return 0f;
            }

            var ticksAhead = unchecked((int)(tick - LatestServerTick));
            return (ticksAhead / (float)TickRateHz) - SecondsSinceLatestTick;
        }
    }
}
