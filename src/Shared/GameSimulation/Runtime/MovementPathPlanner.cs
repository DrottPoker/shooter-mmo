using System;
using System.Collections.Generic;

namespace Mmorpg.GameSimulation
{
    public enum MovementPathPlanStatus : byte
    {
        // The plan ends at the requested destination.
        Reached = 0,

        // The destination cannot be reached, so the plan ends at the closest reachable point.
        Closest = 1,

        // No reachable point is closer to the destination than the start.
        NoPath = 2
    }

    public sealed class MovementPathPlan
    {
        private static readonly SimulationVector3[] NoWaypoints = new SimulationVector3[0];

        internal MovementPathPlan(
            MovementPathPlanStatus status,
            SimulationVector3[] waypoints,
            bool isTruncated,
            int expandedNodes)
        {
            Status = status;
            Waypoints = waypoints ?? NoWaypoints;
            IsTruncated = isTruncated;
            ExpandedNodes = expandedNodes;
        }

        public MovementPathPlanStatus Status { get; }

        // The waypoints after the start, at most MovementPath.MaximumWaypoints.
        public IReadOnlyList<SimulationVector3> Waypoints { get; }

        // The route continued beyond the last waypoint, so a follow-up plan can extend it.
        public bool IsTruncated { get; }

        public int ExpandedNodes { get; }
    }

    public sealed class MovementPathPlannerSettings
    {
        public MovementPathPlannerSettings(
            float cellSize,
            float maximumSearchDistance,
            int maximumExpandedNodes,
            float clearanceMargin,
            float maximumPathLength)
        {
            if (!IsFinite(cellSize) || cellSize < 0.1f || cellSize > 2f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            }

            if (!IsFinite(maximumSearchDistance)
                || maximumSearchDistance < cellSize
                || maximumSearchDistance > MovementPathRules.MaximumLength)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSearchDistance));
            }

            if (maximumExpandedNodes < 1 || maximumExpandedNodes > 1_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumExpandedNodes));
            }

            if (!IsFinite(clearanceMargin) || clearanceMargin < 0f || clearanceMargin > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(clearanceMargin));
            }

            if (!IsFinite(maximumPathLength)
                || maximumPathLength <= 0f
                || maximumPathLength > MovementPathRules.MaximumLength)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPathLength));
            }

            CellSize = cellSize;
            MaximumSearchDistance = maximumSearchDistance;
            MaximumExpandedNodes = maximumExpandedNodes;
            ClearanceMargin = clearanceMargin;
            MaximumPathLength = maximumPathLength;
        }

        // Covers the streamed collision window and stays below the worker's path limit.
        public static MovementPathPlannerSettings Default
        {
            get { return new MovementPathPlannerSettings(0.5f, 64f, 8192, 0.05f, 192f); }
        }

        public float CellSize { get; }

        // Planar meters from the start that the search may explore.
        public float MaximumSearchDistance { get; }

        public int MaximumExpandedNodes { get; }

        // Extra radius kept between the capsule and walls on planned routes.
        public float ClearanceMargin { get; }

        public float MaximumPathLength { get; }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // Plans click-to-move routes with A* on a fine internal grid sampled from the
    // collision world. A cell can hold several walkable levels, so platforms, ramps, and
    // stairs work, and the player never sees the grid. The route is string pulled into
    // few waypoints. The same input always gives the same route, but correctness does
    // not depend on it, because the route travels to the worker as data. Not thread-safe.
    public sealed class MovementPathPlanner
    {
        private const float HeightEpsilon = 0.01f;
        private const float LevelBucketSize = 0.05f;
        private const float FlatGroundNormalY = 0.999f;
        private const float StartGroundSearchDepth = 4f;
        private const float MaximumSampleSpacing = 0.25f;
        private const float SameLevelTolerance = 0.1f;
        private const float DiagonalFactor = 1.41421356f;
        private const float GoalProjectionRadius = 4f;

        private static readonly int[] NeighborX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] NeighborZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        private readonly ICollisionWorld collisionWorld;
        private readonly MovementSimulationSettings movement;
        private readonly MovementPathPlannerSettings settings;
        private readonly CharacterCollisionSettings collision;
        private readonly float clearanceRadius;
        private readonly float clearanceLift;
        private readonly float clearanceHeight;
        private readonly float slopeTangent;
        private readonly float sampleSpacing;
        private readonly CollisionQueryBuffer queryBuffer = new CollisionQueryBuffer();
        private readonly List<Node> nodes = new List<Node>();
        private readonly Dictionary<NodeKey, int> nodeIndices = new Dictionary<NodeKey, int>();
        private readonly Dictionary<NodeKey, Probe> probes = new Dictionary<NodeKey, Probe>();
        private readonly List<OpenEntry> open = new List<OpenEntry>();
        private readonly List<RoutePoint> route = new List<RoutePoint>();
        private readonly List<RoutePoint> pulled = new List<RoutePoint>();
        private readonly int[] neighborNodes = new int[8];

        public MovementPathPlanner(
            ICollisionWorld collisionWorld,
            MovementSimulationSettings movement,
            MovementPathPlannerSettings settings)
        {
            this.collisionWorld = collisionWorld ?? throw new ArgumentNullException(nameof(collisionWorld));
            this.movement = movement ?? throw new ArgumentNullException(nameof(movement));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            collision = movement.CharacterCollision;
            clearanceRadius = (float)(collision.Radius + settings.ClearanceMargin);
            // Obstacles up to the step height are steps, so the clearance capsule starts
            // above them and still reaches the full character height.
            clearanceLift = Math.Min(
                (float)(collision.StepHeight + HeightEpsilon),
                (float)(collision.Height - (float)(clearanceRadius * 2f)));
            if (clearanceLift < 0f)
            {
                throw new ArgumentException(
                    "The clearance margin leaves no room for the character capsule.",
                    nameof(settings));
            }

            clearanceHeight = (float)(collision.Height - clearanceLift);
            slopeTangent = (float)Math.Tan(collision.MaximumSlopeDegrees * (Math.PI / 180d));
            sampleSpacing = Math.Min(MaximumSampleSpacing, (float)(settings.CellSize * 0.5f));
        }

        public MovementPathPlan Plan(SimulationVector3 start, SimulationVector3 destination)
        {
            if (!SimulationVector3.IsFinite(start) || !SimulationVector3.IsFinite(destination))
            {
                return new MovementPathPlan(MovementPathPlanStatus.NoPath, null, false, 0);
            }

            ResetSearch();
            if (!TryFindGround(start, collision.StepHeight, StartGroundSearchDepth, out var startPoint))
            {
                return new MovementPathPlan(MovementPathPlanStatus.NoPath, null, false, 0);
            }

            destination = new SimulationVector3(
                Clamp(destination.X, movement.MinimumX, movement.MaximumX),
                destination.Y,
                Clamp(destination.Z, movement.MinimumZ, movement.MaximumZ));
            var planarDistance = (float)Math.Sqrt(PlanarDistanceSquared(startPoint.Position, destination));
            if (planarDistance <= PlayerMovementSimulation.WaypointArrivalDistance)
            {
                return new MovementPathPlan(MovementPathPlanStatus.NoPath, null, false, 0);
            }

            // A destination beyond the search area moves toward the start along the line.
            var exact = true;
            var searchLimit = (float)(settings.MaximumSearchDistance - settings.CellSize);
            if (planarDistance > searchLimit)
            {
                var amount = (float)(searchLimit / planarDistance);
                destination = new SimulationVector3(
                    (float)(startPoint.Position.X + (float)((float)(destination.X - startPoint.Position.X) * amount)),
                    (float)(startPoint.Position.Y + (float)((float)(destination.Y - startPoint.Position.Y) * amount)),
                    (float)(startPoint.Position.Z + (float)((float)(destination.Z - startPoint.Position.Z) * amount)));
                exact = false;
            }

            var destinationReachable = TryFindGround(
                    destination,
                    collision.StepHeight,
                    collision.GroundSnapDistance,
                    out var goalPoint)
                && IsClear(goalPoint.Position);
            if (!destinationReachable)
            {
                // A wall face or a spot too close to geometry plans to the nearest free point.
                destinationReachable = TryProjectGoal(destination, out goalPoint);
                exact = false;
            }

            var reachedStatus = exact ? MovementPathPlanStatus.Reached : MovementPathPlanStatus.Closest;
            if (destinationReachable && IsSegmentWalkable(startPoint, goalPoint, true))
            {
                route.Add(startPoint);
                route.Add(goalPoint);
                return Finish(reachedStatus, 0);
            }

            if (destinationReachable)
            {
                destination = goalPoint.Position;
            }

            var goalCellX = CellCoordinate(destination.X);
            var goalCellZ = CellCoordinate(destination.Z);
            var goalHeight = destination.Y;
            var startIndex = AddNode(
                CellCoordinate(startPoint.Position.X),
                CellCoordinate(startPoint.Position.Z),
                startPoint);
            var startNode = nodes[startIndex];
            startNode.Clearance = 1;
            startNode.G = 0f;
            nodes[startIndex] = startNode;
            Push(startIndex, 0f, Heuristic(startNode, goalCellX, goalCellZ));

            var bestIndex = startIndex;
            var bestScore = DistanceSquared(startPoint.Position, destination);
            var goalIndex = -1;
            var expanded = 0;
            while (open.Count > 0 && expanded < settings.MaximumExpandedNodes)
            {
                var entry = Pop();
                var current = nodes[entry.Node];
                if (current.Closed || entry.G > current.G)
                {
                    continue;
                }

                current.Closed = true;
                nodes[entry.Node] = current;
                expanded++;
                if (current.CellX == goalCellX
                    && current.CellZ == goalCellZ
                    && Math.Abs((float)(current.Point.Position.Y - goalHeight))
                        <= (float)(collision.StepHeight + HeightEpsilon))
                {
                    goalIndex = entry.Node;
                    break;
                }

                ExpandNeighbors(entry.Node, startPoint.Position, destination, goalCellX, goalCellZ, ref bestIndex, ref bestScore);
            }

            if (goalIndex >= 0)
            {
                BuildRoute(goalIndex);
                if (destinationReachable && IsSegmentWalkable(route[route.Count - 1], goalPoint, false))
                {
                    route.Add(goalPoint);
                    return Finish(reachedStatus, expanded);
                }

                return Finish(MovementPathPlanStatus.Closest, expanded);
            }

            if (bestIndex == startIndex)
            {
                return new MovementPathPlan(MovementPathPlanStatus.NoPath, null, false, expanded);
            }

            BuildRoute(bestIndex);
            return Finish(MovementPathPlanStatus.Closest, expanded);
        }

        private void ExpandNeighbors(
            int currentIndex,
            SimulationVector3 start,
            SimulationVector3 destination,
            int goalCellX,
            int goalCellZ,
            ref int bestIndex,
            ref float bestScore)
        {
            var current = nodes[currentIndex];
            for (var neighbor = 0; neighbor < NeighborX.Length; neighbor++)
            {
                neighborNodes[neighbor] = -1;
                var deltaX = NeighborX[neighbor];
                var deltaZ = NeighborZ[neighbor];

                // A diagonal move needs both orthogonal cells, so it never cuts a corner.
                if (neighbor >= 4
                    && (neighborNodes[deltaX > 0 ? 0 : 1] < 0
                        || neighborNodes[deltaZ > 0 ? 2 : 3] < 0))
                {
                    continue;
                }

                var cellX = current.CellX + deltaX;
                var cellZ = current.CellZ + deltaZ;
                var centerX = CellCenter(cellX);
                var centerZ = CellCenter(cellZ);
                if (centerX < movement.MinimumX
                    || centerX > movement.MaximumX
                    || centerZ < movement.MinimumZ
                    || centerZ > movement.MaximumZ)
                {
                    continue;
                }

                var fromStartX = (float)(centerX - start.X);
                var fromStartZ = (float)(centerZ - start.Z);
                var searchDistance = settings.MaximumSearchDistance;
                if ((float)((float)(fromStartX * fromStartX) + (float)(fromStartZ * fromStartZ))
                    > (float)(searchDistance * searchDistance))
                {
                    continue;
                }

                if (!TryStep(current, cellX, cellZ, centerX, centerZ, out var point))
                {
                    continue;
                }

                var index = AddNode(cellX, cellZ, point);
                if (!IsNodeClear(index))
                {
                    continue;
                }

                neighborNodes[neighbor] = index;
                var next = nodes[index];
                if (next.Closed)
                {
                    continue;
                }

                var cost = Distance(current.Point.Position, next.Point.Position);
                var tentative = (float)(current.G + cost);
                if (tentative >= next.G)
                {
                    continue;
                }

                next.G = tentative;
                next.Parent = currentIndex;
                nodes[index] = next;
                Push(index, tentative, (float)(tentative + Heuristic(next, goalCellX, goalCellZ)));

                var score = DistanceSquared(next.Point.Position, destination);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }
        }

        // Probes the walkable surface of a neighbor cell from the current level.
        private bool TryStep(
            Node current,
            int cellX,
            int cellZ,
            float centerX,
            float centerZ,
            out RoutePoint point)
        {
            // Cached probes use the widest window, the diagonal, so the same key always
            // returns the same surface. The height checks below use the real distance.
            var maximumDistance = (float)(settings.CellSize * DiagonalFactor);
            var key = new NodeKey(cellX, cellZ, current.Level);
            if (!probes.TryGetValue(key, out var probe))
            {
                var referenceHeight = (float)(current.Level * LevelBucketSize);
                var slopeRise = (float)(maximumDistance * slopeTangent);
                var rise = (float)((float)(Math.Max(collision.StepHeight, slopeRise) + LevelBucketSize)
                    + HeightEpsilon);
                var drop = (float)((float)(Math.Max(collision.GroundSnapDistance, slopeRise)
                    + LevelBucketSize) + HeightEpsilon);
                probe = TryFindGround(
                    new SimulationVector3(centerX, referenceHeight, centerZ),
                    rise,
                    drop,
                    out var probed)
                    ? new Probe(true, probed)
                    : new Probe(false, default(RoutePoint));
                probes.Add(key, probe);
            }

            point = probe.Point;
            if (!probe.Found)
            {
                return false;
            }

            var distance = (float)Math.Sqrt(PlanarDistanceSquared(current.Point.Position, point.Position));
            if (!IsHeightChangeWalkable(current.Point, point, distance))
            {
                return false;
            }

            // A height change faster than a step must follow one continuous slope.
            var delta = (float)(point.Position.Y - current.Point.Position.Y);
            if (delta > (float)(collision.StepHeight + HeightEpsilon)
                || (float)(-delta) > (float)(collision.GroundSnapDistance + HeightEpsilon))
            {
                var middle = new SimulationVector3(
                    (float)((float)(current.Point.Position.X + point.Position.X) * 0.5f),
                    current.Point.Position.Y,
                    (float)((float)(current.Point.Position.Z + point.Position.Z) * 0.5f));
                var halfDistance = (float)(distance * 0.5f);
                var halfRise = (float)(Math.Max(collision.StepHeight, (float)(halfDistance * slopeTangent))
                    + HeightEpsilon);
                if (!TryFindGround(middle, halfRise, halfRise, out var middlePoint)
                    || !IsHeightChangeWalkable(current.Point, middlePoint, halfDistance)
                    || !IsHeightChangeWalkable(middlePoint, point, halfDistance))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsHeightChangeWalkable(RoutePoint from, RoutePoint to, float planarDistance)
        {
            var delta = (float)(to.Position.Y - from.Position.Y);
            var slopeRise = (float)((float)(planarDistance * slopeTangent) + HeightEpsilon);
            var onSlope = from.NormalY < FlatGroundNormalY || to.NormalY < FlatGroundNormalY;
            if (delta > (float)(collision.StepHeight + HeightEpsilon))
            {
                return onSlope && delta <= slopeRise;
            }

            if ((float)(-delta) > (float)(collision.GroundSnapDistance + HeightEpsilon))
            {
                return onSlope && (float)(-delta) <= slopeRise;
            }

            return true;
        }

        // Walks a straight planar segment in small samples with the same step, slope, and
        // clearance rules as the grid. The relaxed start lets a character that touches a
        // wall leave it, because the motor allows contact closer than the margin.
        private bool IsSegmentWalkable(RoutePoint from, RoutePoint to, bool relaxedStart)
        {
            var distance = (float)Math.Sqrt(PlanarDistanceSquared(from.Position, to.Position));
            var sampleCount = Math.Max(1, (int)Math.Ceiling((float)(distance / sampleSpacing)));
            var stepDistance = (float)(distance / sampleCount);
            var slopeRise = (float)(stepDistance * slopeTangent);
            var rise = (float)(Math.Max(collision.StepHeight, slopeRise) + HeightEpsilon);
            var drop = (float)(Math.Max(collision.GroundSnapDistance, slopeRise) + HeightEpsilon);
            var relaxedDistanceSquared = relaxedStart
                ? (float)(clearanceRadius * clearanceRadius)
                : 0f;
            var current = from;
            for (var sample = 1; sample <= sampleCount; sample++)
            {
                var amount = (float)((float)sample / sampleCount);
                var sampleX = sample == sampleCount
                    ? to.Position.X
                    : (float)(from.Position.X + (float)((float)(to.Position.X - from.Position.X) * amount));
                var sampleZ = sample == sampleCount
                    ? to.Position.Z
                    : (float)(from.Position.Z + (float)((float)(to.Position.Z - from.Position.Z) * amount));
                if (!TryFindGround(
                        new SimulationVector3(sampleX, current.Position.Y, sampleZ),
                        rise,
                        drop,
                        out var next)
                    || !IsHeightChangeWalkable(current, next, stepDistance))
                {
                    return false;
                }

                if (PlanarDistanceSquared(from.Position, next.Position) > relaxedDistanceSquared
                    && !IsClear(next.Position))
                {
                    return false;
                }

                current = next;
            }

            return Math.Abs((float)(current.Position.Y - to.Position.Y)) <= SameLevelTolerance;
        }

        private bool TryFindGround(
            SimulationVector3 position,
            float maximumRise,
            float maximumDrop,
            out RoutePoint point)
        {
            if (!KinematicCharacterMotor.TryFindWalkableGround(
                    collisionWorld,
                    collision,
                    position,
                    maximumRise,
                    maximumDrop,
                    queryBuffer,
                    out var height,
                    out var normal))
            {
                point = default(RoutePoint);
                return false;
            }

            point = new RoutePoint(new SimulationVector3(position.X, height, position.Z), normal.Y);
            return true;
        }

        // Searches square rings around the destination for the nearest walkable, clear cell.
        private bool TryProjectGoal(SimulationVector3 destination, out RoutePoint goal)
        {
            goal = default(RoutePoint);
            var centerX = CellCoordinate(destination.X);
            var centerZ = CellCoordinate(destination.Z);
            var ringCount = (int)Math.Ceiling((float)(GoalProjectionRadius / settings.CellSize));
            var drop = (float)(collision.Height + collision.StepHeight);
            for (var ring = 0; ring <= ringCount; ring++)
            {
                var found = false;
                var bestDistance = float.PositiveInfinity;
                for (var offsetZ = -ring; offsetZ <= ring; offsetZ++)
                {
                    for (var offsetX = -ring; offsetX <= ring; offsetX++)
                    {
                        if (Math.Abs(offsetX) != ring && Math.Abs(offsetZ) != ring)
                        {
                            continue;
                        }

                        var cellX = CellCenter(centerX + offsetX);
                        var cellZ = CellCenter(centerZ + offsetZ);
                        if (cellX < movement.MinimumX
                            || cellX > movement.MaximumX
                            || cellZ < movement.MinimumZ
                            || cellZ > movement.MaximumZ
                            || !TryFindGround(
                                new SimulationVector3(cellX, destination.Y, cellZ),
                                collision.StepHeight,
                                drop,
                                out var candidate)
                            || !IsClear(candidate.Position))
                        {
                            continue;
                        }

                        var distance = DistanceSquared(candidate.Position, destination);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            goal = candidate;
                            found = true;
                        }
                    }
                }

                if (found)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsNodeClear(int index)
        {
            var node = nodes[index];
            if (node.Clearance == 0)
            {
                node.Clearance = IsClear(node.Point.Position) ? (sbyte)1 : (sbyte)-1;
                nodes[index] = node;
            }

            return node.Clearance > 0;
        }

        private bool IsClear(SimulationVector3 supportPosition)
        {
            var root = new SimulationVector3(
                supportPosition.X,
                (float)(supportPosition.Y + clearanceLift),
                supportPosition.Z);
            var bounds = new CollisionAabb(
                new SimulationVector3(
                    (float)(root.X - clearanceRadius),
                    root.Y,
                    (float)(root.Z - clearanceRadius)),
                new SimulationVector3(
                    (float)(root.X + clearanceRadius),
                    (float)(root.Y + clearanceHeight),
                    (float)(root.Z + clearanceRadius)));
            queryBuffer.Clear();
            collisionWorld.QueryShapes(bounds, CollisionLayers.CharacterMovement, queryBuffer);
            for (var index = 0; index < queryBuffer.Shapes.Count; index++)
            {
                if (queryBuffer.Shapes[index].TryGetCapsulePenetration(
                        root,
                        clearanceRadius,
                        clearanceHeight,
                        out _,
                        out var depth)
                    && depth > HeightEpsilon * 0.1f)
                {
                    return false;
                }
            }

            return true;
        }

        private MovementPathPlan Finish(MovementPathPlanStatus status, int expanded)
        {
            PullRoute();
            var waypoints = new List<SimulationVector3>(Math.Min(pulled.Count, MovementPath.MaximumWaypoints));
            var isTruncated = false;
            var length = 0f;
            var previous = route[0].Position;
            for (var index = 0; index < pulled.Count; index++)
            {
                var waypoint = pulled[index].Position;
                var segment = (float)Math.Sqrt(PlanarDistanceSquared(previous, waypoint));
                if (waypoints.Count == MovementPath.MaximumWaypoints
                    || (float)(length + segment) > settings.MaximumPathLength)
                {
                    isTruncated = true;
                    break;
                }

                waypoints.Add(waypoint);
                length = (float)(length + segment);
                previous = waypoint;
            }

            if (waypoints.Count == 0)
            {
                return new MovementPathPlan(MovementPathPlanStatus.NoPath, null, false, expanded);
            }

            return new MovementPathPlan(status, waypoints.ToArray(), isTruncated, expanded);
        }

        // Keeps a route point only where the straight segment from the previous kept point
        // stops being walkable. The first segment is a validated grid edge.
        private void PullRoute()
        {
            pulled.Clear();
            var anchor = 0;
            while (anchor < route.Count - 1)
            {
                var next = anchor + 1;
                while (next + 1 < route.Count
                    && IsSegmentWalkable(route[anchor], route[next + 1], anchor == 0))
                {
                    next++;
                }

                pulled.Add(route[next]);
                anchor = next;
            }
        }

        private void BuildRoute(int lastIndex)
        {
            route.Clear();
            for (var index = lastIndex; index >= 0; index = nodes[index].Parent)
            {
                route.Add(nodes[index].Point);
            }

            route.Reverse();
        }

        private int AddNode(int cellX, int cellZ, RoutePoint point)
        {
            var key = new NodeKey(cellX, cellZ, LevelOf(point.Position.Y));
            if (nodeIndices.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var index = nodes.Count;
            nodes.Add(new Node(cellX, cellZ, key.Level, point));
            nodeIndices.Add(key, index);
            return index;
        }

        private float Heuristic(Node node, int goalCellX, int goalCellZ)
        {
            // Octile distance between cell centers never exceeds the cost of a grid route.
            var deltaX = Math.Abs(node.CellX - goalCellX);
            var deltaZ = Math.Abs(node.CellZ - goalCellZ);
            var diagonal = Math.Min(deltaX, deltaZ);
            var straight = Math.Max(deltaX, deltaZ) - diagonal;
            return (float)((float)((float)(diagonal * DiagonalFactor) + straight) * settings.CellSize);
        }

        private void Push(int node, float g, float f)
        {
            var entry = new OpenEntry(f, g, node);
            open.Add(entry);
            var child = open.Count - 1;
            while (child > 0)
            {
                var parent = (child - 1) / 2;
                if (!IsBefore(open[child], open[parent]))
                {
                    break;
                }

                var swap = open[parent];
                open[parent] = open[child];
                open[child] = swap;
                child = parent;
            }
        }

        private OpenEntry Pop()
        {
            var first = open[0];
            var last = open.Count - 1;
            open[0] = open[last];
            open.RemoveAt(last);
            var parent = 0;
            while (true)
            {
                var left = (parent * 2) + 1;
                if (left >= open.Count)
                {
                    break;
                }

                var right = left + 1;
                var child = right < open.Count && IsBefore(open[right], open[left]) ? right : left;
                if (!IsBefore(open[child], open[parent]))
                {
                    break;
                }

                var swap = open[parent];
                open[parent] = open[child];
                open[child] = swap;
                parent = child;
            }

            return first;
        }

        // Equal estimates prefer the deeper node, then the older node, so ties are stable.
        private static bool IsBefore(OpenEntry left, OpenEntry right)
        {
            if (left.F != right.F)
            {
                return left.F < right.F;
            }

            if (left.G != right.G)
            {
                return left.G > right.G;
            }

            return left.Node < right.Node;
        }

        private void ResetSearch()
        {
            nodes.Clear();
            nodeIndices.Clear();
            probes.Clear();
            open.Clear();
            route.Clear();
            pulled.Clear();
        }

        private int CellCoordinate(float position)
        {
            return (int)Math.Floor((float)(position / settings.CellSize));
        }

        private float CellCenter(int cell)
        {
            return (float)((float)(cell + 0.5f) * settings.CellSize);
        }

        private static int LevelOf(float height)
        {
            return (int)Math.Floor((float)((float)(height / LevelBucketSize) + 0.5f));
        }

        private static float Distance(SimulationVector3 left, SimulationVector3 right)
        {
            return (float)Math.Sqrt(DistanceSquared(left, right));
        }

        private static float DistanceSquared(SimulationVector3 left, SimulationVector3 right)
        {
            var deltaX = (float)(left.X - right.X);
            var deltaY = (float)(left.Y - right.Y);
            var deltaZ = (float)(left.Z - right.Z);
            return (float)((float)((float)(deltaX * deltaX) + (float)(deltaY * deltaY))
                + (float)(deltaZ * deltaZ));
        }

        private static float PlanarDistanceSquared(SimulationVector3 left, SimulationVector3 right)
        {
            var deltaX = (float)(left.X - right.X);
            var deltaZ = (float)(left.Z - right.Z);
            return (float)((float)(deltaX * deltaX) + (float)(deltaZ * deltaZ));
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private readonly struct RoutePoint
        {
            public RoutePoint(SimulationVector3 position, float normalY)
            {
                Position = position;
                NormalY = normalY;
            }

            public SimulationVector3 Position { get; }

            public float NormalY { get; }
        }

        private readonly struct Probe
        {
            public Probe(bool found, RoutePoint point)
            {
                Found = found;
                Point = point;
            }

            public bool Found { get; }

            public RoutePoint Point { get; }
        }

        private readonly struct NodeKey : IEquatable<NodeKey>
        {
            public NodeKey(int cellX, int cellZ, int level)
            {
                CellX = cellX;
                CellZ = cellZ;
                Level = level;
            }

            public int CellX { get; }

            public int CellZ { get; }

            public int Level { get; }

            public bool Equals(NodeKey other)
            {
                return CellX == other.CellX && CellZ == other.CellZ && Level == other.Level;
            }

            public override bool Equals(object obj)
            {
                return obj is NodeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = CellX;
                    hashCode = (hashCode * 397) ^ CellZ;
                    hashCode = (hashCode * 397) ^ Level;
                    return hashCode;
                }
            }
        }

        private struct Node
        {
            public Node(int cellX, int cellZ, int level, RoutePoint point)
            {
                CellX = cellX;
                CellZ = cellZ;
                Level = level;
                Point = point;
                G = float.PositiveInfinity;
                Parent = -1;
                Closed = false;
                Clearance = 0;
            }

            public int CellX;
            public int CellZ;
            public int Level;
            public RoutePoint Point;
            public float G;
            public int Parent;
            public bool Closed;

            // 0 is not yet tested, 1 is clear, and -1 is blocked.
            public sbyte Clearance;
        }

        private readonly struct OpenEntry
        {
            public OpenEntry(float f, float g, int node)
            {
                F = f;
                G = g;
                Node = node;
            }

            public float F { get; }

            public float G { get; }

            public int Node { get; }
        }
    }
}
