using System;
using System.Collections.Generic;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Gameplay
{
    // The buffered server states of one remote entity. A state that moved from the one before
    // is a moving state; one at the same place says the entity stands there. Samples arrive at
    // uneven intervals: every snapshot, every few, or once a second for an entity that stands.
    internal sealed class RemoteMovementInterpolation
    {
        private const int MaximumBufferedSnapshots = 32;

        // Less than this squared distance is the same place; a record moves in steps of 7.8 mm.
        private const float SamePlaceDistanceSquared = 0.000001f;

        private readonly List<Sample> samples = new List<Sample>();
        private uint latestRawServerTick;

        public int Count
        {
            get { return samples.Count; }
        }

        public long LatestServerTick { get; private set; }

        // The newest server tick as it arrived, before unwrapping.
        public uint LatestRawServerTick
        {
            get { return latestRawServerTick; }
        }

        // Whether the newest state moved from the one before it.
        public bool NewestMoved
        {
            get { return samples.Count > 0 && samples[samples.Count - 1].Moved; }
        }

        // The ticks between the two newest states when both moved, the interval at which a
        // walking entity's states arrive; 0 when the newest state did not follow a moving one.
        public long LastMovingIntervalTicks { get; private set; }

        public void Clear()
        {
            samples.Clear();
            latestRawServerTick = 0;
            LatestServerTick = 0;
            LastMovingIntervalTicks = 0;
        }

        public bool TryGetNewest(out PlayerMovementState state)
        {
            if (samples.Count == 0)
            {
                state = default;
                return false;
            }

            state = samples[samples.Count - 1].State;
            return true;
        }

        // The planar velocity in meters per second between the two newest states, which a
        // remote record does not carry. False with fewer than two states.
        public bool TryGetNewestPlanarVelocity(int tickRateHz, out float velocityX, out float velocityZ)
        {
            velocityX = 0f;
            velocityZ = 0f;
            if (samples.Count < 2)
            {
                return false;
            }

            var newest = samples[samples.Count - 1];
            var previous = samples[samples.Count - 2];
            var seconds = (newest.ServerTick - previous.ServerTick) / (float)Math.Max(1, tickRateHz);
            if (seconds <= 0f)
            {
                return false;
            }

            velocityX = (newest.State.PositionX - previous.State.PositionX) / seconds;
            velocityZ = (newest.State.PositionZ - previous.State.PositionZ) / seconds;
            return true;
        }

        public bool Push(uint serverTick, PlayerMovementState state)
        {
            return Push(serverTick, state, long.MaxValue);
        }

        // Buffers a newer state. A moving state that follows a standing one by more than
        // expectedIntervalTicks starts one expected interval before it: the entity stood until
        // then, so it neither glides from the old state nor starts late.
        public bool Push(uint serverTick, PlayerMovementState state, long expectedIntervalTicks)
        {
            if (samples.Count == 0)
            {
                latestRawServerTick = serverTick;
                LatestServerTick = serverTick;
                Add(new Sample(LatestServerTick, state, moved: false));
                return true;
            }

            if (!MovementSequence.IsNewer(serverTick, latestRawServerTick))
            {
                return false;
            }

            var previous = samples[samples.Count - 1];
            var tick = LatestServerTick + unchecked(serverTick - latestRawServerTick);
            var moved = !IsSamePlace(previous.State, state);
            LastMovingIntervalTicks = 0;
            if (moved && !previous.Moved && tick - previous.ServerTick > expectedIntervalTicks)
            {
                Add(new Sample(tick - expectedIntervalTicks, previous.State, moved: false));
            }
            else if (moved && previous.Moved)
            {
                LastMovingIntervalTicks = tick - previous.ServerTick;
            }

            LatestServerTick = tick;
            latestRawServerTick = serverTick;
            Add(new Sample(tick, state, moved));
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

        private static bool IsSamePlace(PlayerMovementState first, PlayerMovementState second)
        {
            var deltaX = first.PositionX - second.PositionX;
            var deltaY = first.PositionY - second.PositionY;
            var deltaZ = first.PositionZ - second.PositionZ;
            return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <= SamePlaceDistanceSquared;
        }

        private void Add(Sample sample)
        {
            samples.Add(sample);
            if (samples.Count > MaximumBufferedSnapshots)
            {
                samples.RemoveAt(0);
            }
        }

        private readonly struct Sample
        {
            public Sample(long serverTick, PlayerMovementState state, bool moved)
            {
                ServerTick = serverTick;
                State = state;
                Moved = moved;
            }

            public long ServerTick { get; }

            public PlayerMovementState State { get; }

            public bool Moved { get; }
        }
    }
}
