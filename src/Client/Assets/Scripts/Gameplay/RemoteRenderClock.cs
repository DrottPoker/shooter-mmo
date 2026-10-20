using System;

namespace Mmorpg.Client.Gameplay
{
    // Plays remote server states back a fixed delay behind the server. The server time comes
    // from a smooth estimate that advances with real time between snapshots and follows their
    // earliest arrivals, because a late packet only delays the newest tick and never advances
    // the server. Measuring the delay against the newest received tick instead made the delay
    // shrink between snapshots and grow at each one, so the clock sped remote characters up and
    // slowed them down at the snapshot rate.
    public sealed class RemoteRenderClock
    {
        private const double DelayToleranceTicks = 0.25d;
        private const double MaximumCatchUpMultiplier = 1.5d;
        private const double MinimumSlowDownMultiplier = 0.25d;

        // An arrival behind the estimate pulls it this share of the way back, so one unusually
        // early packet, or ticks the server skipped, cannot hold it ahead of the others for
        // long, while ordinary jitter barely moves it.
        private const double LateArrivalSmoothing = 0.05d;

        private double estimatedServerTick;
        private long observedLatestServerTick;

        public double CurrentTick { get; private set; }

        // The server tick the clock estimates the server has reached now.
        public double EstimatedServerTick
        {
            get { return estimatedServerTick; }
        }

        public bool IsInitialized { get; private set; }

        public void Clear()
        {
            CurrentTick = 0d;
            estimatedServerTick = 0d;
            observedLatestServerTick = 0;
            IsInitialized = false;
        }

        public double Reset(long latestServerTick, int interpolationDelayTicks)
        {
            Validate(interpolationDelayTicks, 1, 0f);
            estimatedServerTick = latestServerTick;
            observedLatestServerTick = latestServerTick;
            CurrentTick = Math.Max(0d, latestServerTick - interpolationDelayTicks);
            IsInitialized = true;
            return CurrentTick;
        }

        public double Advance(
            float unscaledDeltaTime,
            int tickRateHz,
            long latestServerTick,
            int interpolationDelayTicks)
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
                estimatedServerTick = latestServerTick > estimatedServerTick
                    ? latestServerTick
                    : estimatedServerTick
                        + ((latestServerTick - estimatedServerTick) * LateArrivalSmoothing);
            }

            var desiredTick = Math.Max(0d, estimatedServerTick - interpolationDelayTicks);
            var snapThresholdTicks = Math.Max(
                interpolationDelayTicks * 2d,
                tickRateHz * 0.5d);
            if (desiredTick - CurrentTick >= snapThresholdTicks)
            {
                CurrentTick = Math.Min(latestServerTick, desiredTick);
                return CurrentTick;
            }

            var delayErrorTicks = (estimatedServerTick - CurrentTick) - interpolationDelayTicks;
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

            CurrentTick = Math.Min(
                latestServerTick,
                CurrentTick + (unscaledDeltaTime * tickRateHz * speedMultiplier));
            return CurrentTick;
        }

        private static void Validate(
            int interpolationDelayTicks,
            int tickRateHz,
            float deltaTime)
        {
            if (interpolationDelayTicks <= 0)
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
