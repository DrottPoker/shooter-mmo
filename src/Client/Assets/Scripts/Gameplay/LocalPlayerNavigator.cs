using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Gameplay
{
    public enum NavigationResult
    {
        // The planner is still working on the newest request.
        Planning,

        // A new path replaced the previous one.
        Moving,

        // The player already stands within range, so it stopped instead.
        AlreadyThere,

        // No reachable point brings the player closer, so the previous path continues.
        NoRoute
    }

    // Plans click-to-move paths with the shared planner on a background thread, so a long
    // search never stalls a frame, and hands out path ids on the main thread. Every user
    // request is a new intent; a plan that stopped at the waypoint limit continues under
    // the same intent. The worker follows the same waypoints and owns the collision.
    internal sealed class LocalPlayerNavigator
    {
        private readonly MovementPathPlanner planner;
        private readonly MovementSimulationSettings settings;
        private Task<TimedPlan> runningPlan;
        private NavigationRequest runningRequest;
        private NavigationRequest queuedRequest;
        private float continuationRange;
        private uint lastPathId;

        public LocalPlayerNavigator(
            ICollisionWorld collisionWorld,
            MovementSimulationSettings settings,
            MovementPathPlannerSettings plannerSettings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            planner = new MovementPathPlanner(collisionWorld, settings, plannerSettings);
        }

        // The path the player follows, or null while it stands.
        public MovementPath CurrentPath { get; private set; }

        // The newest user request: a walk, an approach, or a stop.
        public int IntentId { get; private set; }

        // The intent whose plan was applied last.
        public int AppliedIntentId { get; private set; }

        public NavigationResult LastResult { get; private set; } = NavigationResult.NoRoute;

        public MovementPathPlanStatus LastStatus { get; private set; } = MovementPathPlanStatus.NoPath;

        public int LastExpandedNodes { get; private set; }

        public double LastPlanMilliseconds { get; private set; }

        // The destination of a plan that stopped early, so a follow-up plan can continue it.
        public SimulationVector3? ContinuationGoal { get; private set; }

        public bool IsPlanning
        {
            get { return runningPlan != null || queuedRequest != null; }
        }

        // Plans from the predicted position. A range above zero stops the path where it first
        // comes that close to the destination, as approaching an NPC does.
        public int Request(SimulationVector3 start, SimulationVector3 destination, float range)
        {
            IntentId = NextIntentId();
            ContinuationGoal = null;
            Enqueue(new NavigationRequest(IntentId, start, destination, range));
            return IntentId;
        }

        public int Stop()
        {
            IntentId = NextIntentId();
            queuedRequest = null;
            CurrentPath = null;
            ContinuationGoal = null;
            AppliedIntentId = IntentId;
            LastResult = NavigationResult.AlreadyThere;
            return IntentId;
        }

        // Applies a finished plan and starts the next one. Main thread only.
        public void Update(PlayerMovementState predicted)
        {
            if (runningPlan != null && runningPlan.IsCompleted)
            {
                var request = runningRequest;
                var finished = runningPlan;
                runningPlan = null;
                runningRequest = null;
                if (request.IntentId == IntentId)
                {
                    Apply(request, finished);
                }
            }

            ContinueTruncatedPath(predicted);
            StartQueuedPlan();
        }

        private void ContinueTruncatedPath(PlayerMovementState predicted)
        {
            var path = CurrentPath;
            if (path == null
                || !ContinuationGoal.HasValue
                || IsPlanning
                || !predicted.IsGrounded
                || predicted.PathProgress.PathId != path.Id
                || predicted.PathProgress.NextWaypointIndex < path.Count)
            {
                return;
            }

            var goal = ContinuationGoal.Value;
            ContinuationGoal = null;
            Enqueue(new NavigationRequest(IntentId, predicted.Position, goal, continuationRange));
        }

        private void Enqueue(NavigationRequest request)
        {
            // Only the newest request matters, so a waiting one is replaced.
            queuedRequest = request;
            StartQueuedPlan();
        }

        private void StartQueuedPlan()
        {
            if (runningPlan != null || queuedRequest == null)
            {
                return;
            }

            var request = queuedRequest;
            queuedRequest = null;
            runningRequest = request;
            runningPlan = Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var plan = planner.Plan(request.Start, request.Destination);
                return new TimedPlan(plan, stopwatch.Elapsed.TotalMilliseconds);
            });
        }

        private void Apply(NavigationRequest request, Task<TimedPlan> finished)
        {
            AppliedIntentId = request.IntentId;
            if (finished.IsFaulted || finished.IsCanceled)
            {
                LastStatus = MovementPathPlanStatus.NoPath;
                LastResult = NavigationResult.NoRoute;
                return;
            }

            var plan = finished.Result.Plan;
            LastStatus = plan.Status;
            LastExpandedNodes = plan.ExpandedNodes;
            LastPlanMilliseconds = finished.Result.Milliseconds;
            var waypoints = request.Range > 0f
                ? MovementPathGeometry.TrimToRange(request.Start, plan.Waypoints, request.Destination, request.Range)
                : ToArray(plan.Waypoints);
            if (request.Range > 0f && waypoints.Length == 0)
            {
                CurrentPath = null;
                ContinuationGoal = null;
                LastResult = NavigationResult.AlreadyThere;
                return;
            }

            if (waypoints.Length == 0)
            {
                LastResult = NavigationResult.NoRoute;
                return;
            }

            var path = new MovementPath(NextPathId(), waypoints);
            if (!MovementPathRules.IsValid(path, request.Start, settings, out _))
            {
                LastResult = NavigationResult.NoRoute;
                return;
            }

            CurrentPath = path;
            ContinuationGoal = plan.IsTruncated && !EndsWithinRange(path, request.Destination, request.Range)
                ? request.Destination
                : (SimulationVector3?)null;
            continuationRange = request.Range;
            LastResult = NavigationResult.Moving;
        }

        private static bool EndsWithinRange(MovementPath path, SimulationVector3 destination, float range)
        {
            var end = path.Destination;
            var deltaX = (double)end.X - destination.X;
            var deltaZ = (double)end.Z - destination.Z;
            var limit = (double)range + PlayerMovementSimulation.WaypointArrivalDistance;
            return (deltaX * deltaX) + (deltaZ * deltaZ) <= limit * limit;
        }

        private int NextIntentId()
        {
            return IntentId == int.MaxValue ? 1 : IntentId + 1;
        }

        private uint NextPathId()
        {
            unchecked
            {
                lastPathId++;
            }

            if (lastPathId == 0)
            {
                lastPathId = 1;
            }

            return lastPathId;
        }

        private static SimulationVector3[] ToArray(IReadOnlyList<SimulationVector3> values)
        {
            var result = new SimulationVector3[values.Count];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = values[index];
            }

            return result;
        }

        private sealed class NavigationRequest
        {
            public NavigationRequest(int intentId, SimulationVector3 start, SimulationVector3 destination, float range)
            {
                IntentId = intentId;
                Start = start;
                Destination = destination;
                Range = range;
            }

            public int IntentId { get; }

            public SimulationVector3 Start { get; }

            public SimulationVector3 Destination { get; }

            public float Range { get; }
        }

        private readonly struct TimedPlan
        {
            public TimedPlan(MovementPathPlan plan, double milliseconds)
            {
                Plan = plan;
                Milliseconds = milliseconds;
            }

            public MovementPathPlan Plan { get; }

            public double Milliseconds { get; }
        }
    }
}
