using System;
using System.Collections.Generic;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Gameplay
{
    // The buffered server states of one remote entity. A state that moved from the one before
    // is a moving state; one at the same place says the entity stands there; the first state
    // after a clear says neither. Samples arrive at uneven intervals: every snapshot, every
    // few, or once a second for an entity that stands.
    internal sealed class RemoteMovementInterpolation
    {
        private const int MaximumBufferedSnapshots = 32;

        // Less than this squared distance is the same place; a record moves in steps of 7.8 mm.
        private const float SamePlaceDistanceSquared = 0.000001f;

        private readonly List<Sample> samples = new List<Sample>();
        private uint latestRawServerTick;

        // The only state is the first after a clear, and nothing said whether the entity stood
        // there or walked through it.
        private bool firstStateUnknown;

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

        // Whether LastMovingIntervalTicks is the first one observed: the gap between the first
        // state after a clear and a second state that walked all of it.
        public bool IsFirstMovingInterval { get; private set; }

        public void Clear()
        {
            samples.Clear();
            latestRawServerTick = 0;
            LatestServerTick = 0;
            LastMovingIntervalTicks = 0;
            IsFirstMovingInterval = false;
            firstStateUnknown = false;
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
            return Push(serverTick, state, long.MaxValue, 0, false, 0);
        }

        // Buffers a newer state. A moving state that follows a standing one by more than
        // expectedIntervalTicks starts one expected interval before it: the entity stood until
        // then, so it neither glides from the old state nor starts late. The first state after
        // a clear stands only when firstStateStands says so, as for a reliable rest pose;
        // otherwise a moving second state at most longestWalkGapTicks after it walked the whole
        // gap, such as an entity that came into view while it walked. A second state fewer than
        // firstStateRefreshTicks after such a first state takes its place instead: the worker
        // sends a spawn one tick before the entity's first record, and that tick says where the
        // entity is now, not how often its records come.
        public bool Push(
            uint serverTick,
            PlayerMovementState state,
            long expectedIntervalTicks,
            long longestWalkGapTicks,
            bool firstStateStands,
            long firstStateRefreshTicks)
        {
            if (samples.Count == 0)
            {
                latestRawServerTick = serverTick;
                LatestServerTick = serverTick;
                firstStateUnknown = !firstStateStands;
                Add(new Sample(LatestServerTick, state, moved: false));
                return true;
            }

            if (!MovementSequence.IsNewer(serverTick, latestRawServerTick))
            {
                return false;
            }

            var previous = samples[samples.Count - 1];
            var tick = LatestServerTick + unchecked(serverTick - latestRawServerTick);
            if (samples.Count == 1 && firstStateUnknown && tick - previous.ServerTick < firstStateRefreshTicks)
            {
                samples[0] = new Sample(tick, state, moved: false);
                LatestServerTick = tick;
                latestRawServerTick = serverTick;
                LastMovingIntervalTicks = 0;
                IsFirstMovingInterval = false;
                return true;
            }

            var moved = !IsSamePlace(previous.State, state);
            var walkedFromFirst = moved && firstStateUnknown && tick - previous.ServerTick <= longestWalkGapTicks;
            firstStateUnknown = false;
            LastMovingIntervalTicks = 0;
            IsFirstMovingInterval = false;
            if (walkedFromFirst)
            {
                LastMovingIntervalTicks = tick - previous.ServerTick;
                IsFirstMovingInterval = true;
            }
            else if (moved && !previous.Moved && tick - previous.ServerTick > expectedIntervalTicks)
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
