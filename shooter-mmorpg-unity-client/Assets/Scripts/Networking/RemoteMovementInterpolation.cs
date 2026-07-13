using System.Collections.Generic;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
{
    internal sealed class RemoteMovementInterpolation
    {
        private const int MaximumBufferedSnapshots = 32;
        private readonly List<Sample> samples = new List<Sample>();
        private uint latestRawServerTick;

        public int Count
        {
            get { return samples.Count; }
        }

        public long LatestServerTick { get; private set; }

        public bool Push(uint serverTick, PlayerMovementState state)
        {
            if (samples.Count == 0)
            {
                latestRawServerTick = serverTick;
                LatestServerTick = serverTick;
            }
            else
            {
                if (!MovementSequence.IsNewer(serverTick, latestRawServerTick))
                {
                    return false;
                }

                LatestServerTick += unchecked(serverTick - latestRawServerTick);
                latestRawServerTick = serverTick;
            }

            samples.Add(new Sample(LatestServerTick, state));
            if (samples.Count > MaximumBufferedSnapshots)
            {
                samples.RemoveAt(0);
            }

            return true;
        }

        public bool TrySample(double targetServerTick, out PlayerMovementState state)
        {
            if (samples.Count == 0)
            {
                state = default;
                return false;
            }

            while (samples.Count > 2 && samples[1].ServerTick <= targetServerTick)
            {
                samples.RemoveAt(0);
            }

            if (samples.Count == 1 || targetServerTick <= samples[0].ServerTick)
            {
                state = samples[0].State;
                return true;
            }

            var from = samples[0];
            var to = samples[1];
            var tickRange = to.ServerTick - from.ServerTick;
            var amount = tickRange > 0
                ? (float)((targetServerTick - from.ServerTick) / tickRange)
                : 1f;
            state = MovementStateInterpolation.Interpolate(from.State, to.State, amount);
            return true;
        }

        private readonly struct Sample
        {
            public Sample(long serverTick, PlayerMovementState state)
            {
                ServerTick = serverTick;
                State = state;
            }

            public long ServerTick { get; }

            public PlayerMovementState State { get; }
        }
    }
}
