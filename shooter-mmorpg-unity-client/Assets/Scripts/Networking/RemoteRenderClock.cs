using System;

namespace ShooterMmo.Networking
{
    public sealed class RemoteRenderClock
    {
        private const double DelayToleranceTicks = 0.25d;
        private const double MaximumCatchUpMultiplier = 1.5d;
        private const double MinimumSlowDownMultiplier = 0.25d;

        public double CurrentTick { get; private set; }

        public bool IsInitialized { get; private set; }

        public void Clear()
        {
            CurrentTick = 0d;
            IsInitialized = false;
        }

        public double Reset(long latestServerTick, int interpolationDelayTicks)
        {
            Validate(interpolationDelayTicks, 1, 0f);
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

            var desiredTick = Math.Max(0d, latestServerTick - interpolationDelayTicks);
            var snapThresholdTicks = Math.Max(
                interpolationDelayTicks * 2d,
                tickRateHz * 0.5d);
            if (desiredTick - CurrentTick >= snapThresholdTicks)
            {
                CurrentTick = desiredTick;
                return CurrentTick;
            }

            var currentDelayTicks = latestServerTick - CurrentTick;
            var delayErrorTicks = currentDelayTicks - interpolationDelayTicks;
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
