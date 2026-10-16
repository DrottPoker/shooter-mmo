using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    // An immutable click-to-move route. The client plans it, and the client prediction
    // and the worker follow the same waypoints, so both runtimes step identically.
    public sealed class MovementPath
    {
        public const int MaximumWaypoints = 32;

        private readonly SimulationVector3[] waypoints;

        public MovementPath(uint id, IReadOnlyList<SimulationVector3> waypoints)
        {
            if (id == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(id), "A movement path id must not be zero.");
            }

            if (waypoints == null)
            {
                throw new ArgumentNullException(nameof(waypoints));
            }

            if (waypoints.Count < 1 || waypoints.Count > MaximumWaypoints)
            {
                throw new ArgumentException(
                    "A movement path needs 1 to " + MaximumWaypoints + " waypoints.",
                    nameof(waypoints));
            }

            this.waypoints = new SimulationVector3[waypoints.Count];
            for (var index = 0; index < waypoints.Count; index++)
            {
                if (!SimulationVector3.IsFinite(waypoints[index]))
                {
                    throw new ArgumentException("Movement path waypoints must be finite.", nameof(waypoints));
                }

                this.waypoints[index] = waypoints[index];
            }

            Id = id;
        }

        public uint Id { get; }

        public int Count
        {
            get { return waypoints.Length; }
        }

        public SimulationVector3 this[int index]
        {
            get { return waypoints[index]; }
        }

        public SimulationVector3 Destination
        {
            get { return waypoints[waypoints.Length - 1]; }
        }
    }

    // How far a state has followed its movement path. Path id 0 means no path.
    public readonly struct MovementPathProgress : IEquatable<MovementPathProgress>
    {
        public static readonly MovementPathProgress None = default(MovementPathProgress);

        public MovementPathProgress(uint pathId, int nextWaypointIndex, int blockedTicks)
        {
            if (nextWaypointIndex < 0 || nextWaypointIndex > MovementPath.MaximumWaypoints)
            {
                throw new ArgumentOutOfRangeException(nameof(nextWaypointIndex));
            }

            if (blockedTicks < 0 || blockedTicks > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(blockedTicks));
            }

            if (pathId == 0 && (nextWaypointIndex != 0 || blockedTicks != 0))
            {
                throw new ArgumentException("Progress without a path must be empty.");
            }

            PathId = pathId;
            NextWaypointIndex = nextWaypointIndex;
            BlockedTicks = blockedTicks;
        }

        public uint PathId { get; }

        public int NextWaypointIndex { get; }

        public int BlockedTicks { get; }

        public bool IsFollowing(MovementPath path)
        {
            return path != null && path.Id == PathId && NextWaypointIndex < path.Count;
        }

        public bool Equals(MovementPathProgress other)
        {
            return PathId == other.PathId
                && NextWaypointIndex == other.NextWaypointIndex
                && BlockedTicks == other.BlockedTicks;
        }

        public override bool Equals(object obj)
        {
            return obj is MovementPathProgress other && Equals(other);
        }

        public static bool operator ==(MovementPathProgress left, MovementPathProgress right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MovementPathProgress left, MovementPathProgress right)
        {
            return !left.Equals(right);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)PathId;
                hashCode = (hashCode * 397) ^ NextWaypointIndex;
                hashCode = (hashCode * 397) ^ BlockedTicks;
                return hashCode;
            }
        }
    }

    public static class MovementPathGeometry
    {
        // The route from the start up to the first point within the planar range of the
        // target, so approaching a target stops at interaction range instead of on top of
        // it. Returns no waypoints when the start is already within range, and the whole
        // route when it never comes within range. Uses doubles, so both runtimes agree.
        public static SimulationVector3[] TrimToRange(
            SimulationVector3 start,
            IReadOnlyList<SimulationVector3> waypoints,
            SimulationVector3 target,
            float range)
        {
            if (waypoints == null)
            {
                throw new ArgumentNullException(nameof(waypoints));
            }

            if (float.IsNaN(range) || float.IsInfinity(range) || range < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(range));
            }

            var rangeSquared = (double)range * range;
            if (PlanarDistanceSquared(start, target) <= rangeSquared)
            {
                return new SimulationVector3[0];
            }

            var result = new List<SimulationVector3>(waypoints.Count);
            var previous = start;
            for (var index = 0; index < waypoints.Count; index++)
            {
                var waypoint = waypoints[index];
                if (PlanarDistanceSquared(waypoint, target) > rangeSquared)
                {
                    result.Add(waypoint);
                    previous = waypoint;
                    continue;
                }

                // The smallest amount along the segment where the planar distance equals
                // the range. The segment starts outside and ends inside, so it exists.
                var deltaX = (double)waypoint.X - previous.X;
                var deltaZ = (double)waypoint.Z - previous.Z;
                var offsetX = (double)previous.X - target.X;
                var offsetZ = (double)previous.Z - target.Z;
                var a = (deltaX * deltaX) + (deltaZ * deltaZ);
                var b = 2d * ((deltaX * offsetX) + (deltaZ * offsetZ));
                var c = (offsetX * offsetX) + (offsetZ * offsetZ) - rangeSquared;
                var discriminant = Math.Max(0d, (b * b) - (4d * a * c));
                var amount = a <= 0d
                    ? 1d
                    : Math.Max(0d, Math.Min(1d, (-b - Math.Sqrt(discriminant)) / (2d * a)));
                result.Add(new SimulationVector3(
                    (float)(previous.X + (deltaX * amount)),
                    (float)(previous.Y + (((double)waypoint.Y - previous.Y) * amount)),
                    (float)(previous.Z + (deltaZ * amount))));
                return result.ToArray();
            }

            return result.ToArray();
        }

        private static double PlanarDistanceSquared(SimulationVector3 left, SimulationVector3 right)
        {
            var deltaX = (double)left.X - right.X;
            var deltaZ = (double)left.Z - right.Z;
            return (deltaX * deltaX) + (deltaZ * deltaZ);
        }
    }

    // The limits a worker enforces on a client path. A path never lets a character pass
    // collision, so these only bound the work and keep waypoints inside the World.
    public static class MovementPathRules
    {
        // Planar meters from the start position through every waypoint.
        public const float MaximumLength = 256f;

        public static bool IsValid(
            MovementPath path,
            SimulationVector3 start,
            MovementSimulationSettings settings,
            out string error)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var length = 0d;
            var previous = start;
            for (var index = 0; index < path.Count; index++)
            {
                var waypoint = path[index];
                if (waypoint.X < settings.MinimumX
                    || waypoint.X > settings.MaximumX
                    || waypoint.Z < settings.MinimumZ
                    || waypoint.Z > settings.MaximumZ
                    || Math.Abs(waypoint.Y) > CollisionDataFormat.MaximumWorldCoordinate)
                {
                    error = "Movement path waypoint " + index + " is outside the World bounds.";
                    return false;
                }

                var deltaX = (double)waypoint.X - previous.X;
                var deltaZ = (double)waypoint.Z - previous.Z;
                length += Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
                previous = waypoint;
            }

            if (length > MaximumLength)
            {
                error = "Movement path is longer than " + MaximumLength + " meters.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
