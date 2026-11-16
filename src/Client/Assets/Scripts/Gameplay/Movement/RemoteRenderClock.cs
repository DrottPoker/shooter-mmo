using System;

namespace Mmorpg.Client.Gameplay
{
    // Plays remote server states back a fixed delay behind the server. The server time comes
    // from a smooth estimate that advances with real time between snapshots and follows their
    // earliest arrivals, because a late packet only delays the newest tick and never advances
    // the server. Measuring the delay against the newest received tick instead made the delay
    // shrink between snapshots and grow at each one, so the clock sped remote characters up and
    // slowed them down at the snapshot rate. The render time runs on past the newest state at
    // the clock's speed, so a newer state continues it without a jump; the current tick that
    // states are sampled at never passes the newest state, and how far the render time is past
    // it is the overrun, which a presenter may extrapolate. The delay is a number of ticks that
    // may change from frame to frame.
    public sealed class RemoteRenderClock
    {
        private const double DelayToleranceTicks = 0.25d;
        private const double MaximumCatchUpMultiplier = 1.5d;
        private const double MinimumSlowDownMultiplier = 0.25d;

        // An arrival behind the estimate pulls it this share of the way back, so one unusually
        // early packet, or ticks the server skipped, cannot hold it ahead of the others for
        // long, while ordinary jitter barely moves it. An arrival later than the whole render
        // delay means the connection's latency changed, and the estimate moves to it at once.
        private const double LateArrivalSmoothing = 0.05d;

        private double estimatedServerTick;
        private long observedLatestServerTick;

        // The render time, at most the newest state.
        public double CurrentTick { get; private set; }

        // How many ticks the render time is past the newest state; 0 while states are ahead.
        public double OverrunTicks { get; private set; }

        // The render time, also past the newest state.
        public double RenderTick
        {
            get { return CurrentTick + OverrunTicks; }
        }

        // The server tick the clock estimates the server has reached now.
        public double EstimatedServerTick
        {
            get { return estimatedServerTick; }
        }

        public bool IsInitialized { get; private set; }

        public void Clear()
        {
            CurrentTick = 0d;
            OverrunTicks = 0d;
            estimatedServerTick = 0d;
            observedLatestServerTick = 0;
            IsInitialized = false;
        }

        public double Reset(long latestServerTick, double interpolationDelayTicks)
        {
            Validate(interpolationDelayTicks, 1, 0f);
            estimatedServerTick = latestServerTick;
            observedLatestServerTick = latestServerTick;
            CurrentTick = Math.Max(0d, latestServerTick - interpolationDelayTicks);
            OverrunTicks = 0d;
            IsInitialized = true;
            return CurrentTick;
        }

        public double Advance(
            float unscaledDeltaTime,
            int tickRateHz,
            long latestServerTick,
            double interpolationDelayTicks)
        {
            Validate(interpolationDelayTicks, tickRateHz, unscaledDeltaTime);
            if (!IsInitialized)
            {
                return Reset(latestServerTick, interpolationDelayTicks);
            }

            estimatedServerTick += unscaledDeltaTime * tickRateHz;
            if (latestServerTick > observedLatestServerTick)
            {
                observedLatestServerTick = latestServerTick;
                var lateTicks = estimatedServerTick - latestServerTick;
                estimatedServerTick = lateTicks <= 0d || lateTicks > interpolationDelayTicks
                    ? latestServerTick
                    : estimatedServerTick - (lateTicks * LateArrivalSmoothing);
            }

            var desiredTick = Math.Max(0d, estimatedServerTick - interpolationDelayTicks);
            if (desiredTick - CurrentTick >= SnapThresholdTicks(tickRateHz, interpolationDelayTicks))
            {
                SetRenderTick(desiredTick, latestServerTick);
                return CurrentTick;
            }

            var renderTick = RenderTick;
            var delayErrorTicks = (estimatedServerTick - renderTick) - interpolationDelayTicks;
            var speedMultiplier = 1d;
            if (delayErrorTicks > DelayToleranceTicks)
            {
                speedMultiplier += Math.Min(
                    MaximumCatchUpMultiplier - 1d,
                    delayErrorTicks / Math.Max(1, interpolationDelayTicks));
            }
            else if (delayErrorTicks < -DelayToleranceTicks)
            {
                speedMultiplier = Math.Max(
                    MinimumSlowDownMultiplier,
                    1d + (delayErrorTicks / Math.Max(1, interpolationDelayTicks)));
            }

            SetRenderTick(renderTick + (unscaledDeltaTime * tickRateHz * speedMultiplier), latestServerTick);
            return CurrentTick;
        }

        // Whether the render time follows the estimate of the server tick exactly until a newer
        // state arrives: it passed the newest state so far that every Advance sets it to the
        // estimate less the delay. Then one Advance over many frames sets it where Advance frame
        // by frame would, since the estimate runs on with real time alone.
        public bool FollowsEstimate(int tickRateHz, long latestServerTick, double interpolationDelayTicks)
        {
            return IsInitialized
                && observedLatestServerTick == latestServerTick
                && CurrentTick >= latestServerTick
                && Math.Max(0d, estimatedServerTick - interpolationDelayTicks) - CurrentTick
                    >= SnapThresholdTicks(tickRateHz, interpolationDelayTicks);
        }

        // A render time this far behind the delay is set to it at once instead of caught up.
        private static double SnapThresholdTicks(int tickRateHz, double interpolationDelayTicks)
        {
            return Math.Max(interpolationDelayTicks * 2d, tickRateHz * 0.5d);
        }

        private void SetRenderTick(double renderTick, long latestServerTick)
        {
            CurrentTick = Math.Min(latestServerTick, renderTick);
            OverrunTicks = Math.Max(0d, renderTick - latestServerTick);
        }

        private static void Validate(
            double interpolationDelayTicks,
            int tickRateHz,
            float deltaTime)
        {
            if (!(interpolationDelayTicks > 0d) || double.IsInfinity(interpolationDelayTicks))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolationDelayTicks));
            }

            if (tickRateHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRateHz));
            }

            if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
            {
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            }
        }
    }
}
