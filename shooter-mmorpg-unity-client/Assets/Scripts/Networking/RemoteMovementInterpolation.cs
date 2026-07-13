using System;
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
                ? Clamp01((float)((targetServerTick - from.ServerTick) / tickRange))
                : 1f;
            state = Interpolate(from.State, to.State, amount);
            return true;
        }

        private static PlayerMovementState Interpolate(
            PlayerMovementState from,
            PlayerMovementState to,
            float amount)
        {
            return new PlayerMovementState(
                Lerp(from.PositionX, to.PositionX, amount),
                Lerp(from.PositionY, to.PositionY, amount),
                Lerp(from.PositionZ, to.PositionZ, amount),
                Lerp(from.VelocityX, to.VelocityX, amount),
                Lerp(from.VelocityY, to.VelocityY, amount),
                Lerp(from.VelocityZ, to.VelocityZ, amount),
                LerpAngle(from.YawDegrees, to.YawDegrees, amount),
                amount < 0.5f ? from.IsGrounded : to.IsGrounded,
                amount < 0.5f ? from.IsSprinting : to.IsSprinting);
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + ((to - from) * amount);
        }

        private static float LerpAngle(float from, float to, float amount)
        {
            var delta = ((to - from + 540f) % 360f) - 180f;
            var result = (from + (delta * amount)) % 360f;
            return result < 0f ? result + 360f : result;
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
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
