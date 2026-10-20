using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.ActiveSimulationBots;

// Alternates idle intervals with walks to random destinations, like a player clicking around.
// With the World's collision loaded, each walk is planned around obstacles with the shared
// planner on a background thread, as the Unity client plans a click, and the bot keeps its
// previous path until the plan arrives. Without collision, or when the worker runs another
// collision revision, a walk is a straight line that the worker's collision stops.
public sealed class ActiveSimulationBotInputSource : ISimulationBotInputSource
{
    private const double BoundsMargin = 1d;

    // The planner searches 64 meters from the start, so planned walks stay inside that.
    private const double MaximumPlannedWalkDistance = 60d;

    // Stays below the worker's 256 meter path limit for any behavior duration.
    private const double MaximumStraightWalkDistance = 200d;

    private readonly ActiveSimulationBotOptions options;
    private readonly ActiveSimulationBotNavigation? navigation;
    private readonly Random random;
    private uint nextBehaviorTick;
    private uint lastPathId;
    private RealtimeMovementPath? currentPath;
    private Task<MovementPathPlan>? pendingPlan;

    public ActiveSimulationBotInputSource(
        ActiveSimulationBotOptions options,
        int botIndex,
        ActiveSimulationBotNavigation? navigation = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.navigation = navigation;
        random = new Random(HashCode.Combine(options.Seed, botIndex));
    }

    public int PlannedWalks { get; private set; }

    public int StraightWalks { get; private set; }

    public RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        if (pendingPlan is { IsCompleted: true } completed)
        {
            pendingPlan = null;
            ApplyPlan(completed);
        }

        if (nextBehaviorTick == 0 || clientTick >= nextBehaviorTick)
        {
            SelectBehavior(clientTick, joinedSession, position);
        }

        return currentPath;
    }

    private void SelectBehavior(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        var settings = joinedSession.MovementSettings;
        var durationSeconds = RandomBetween(
            options.MinimumBehaviorDuration.TotalSeconds,
            options.MaximumBehaviorDuration.TotalSeconds);
        var durationTicks = Math.Max(1u, (uint)Math.Round(durationSeconds * settings.TickRateHz));
        nextBehaviorTick = clientTick + durationTicks;
        pendingPlan = null;
        if (random.NextDouble() < options.IdleChance)
        {
            currentPath = null;
            return;
        }

        var planned = navigation is not null
            && string.Equals(
                navigation.CollisionWorld.Revision,
                joinedSession.CollisionRevision,
                StringComparison.Ordinal);
        // A destination about one behavior interval of walking away, inside the World.
        var angle = random.NextDouble() * Math.PI * 2d;
        var distance = Math.Min(
            settings.WalkSpeed * durationSeconds,
            planned ? MaximumPlannedWalkDistance : MaximumStraightWalkDistance);
        var x = (float)Math.Clamp(
            position.X + (Math.Sin(angle) * distance),
            settings.MinimumX + BoundsMargin,
            settings.MaximumX - BoundsMargin);
        var z = (float)Math.Clamp(
            position.Z + (Math.Cos(angle) * distance),
            settings.MinimumZ + BoundsMargin,
            settings.MaximumZ - BoundsMargin);
        if (planned)
        {
            pendingPlan = navigation!.TryPlan(
                settings,
                new SimulationVector3(position.X, position.Y, position.Z),
                new SimulationVector3(x, position.Y, z));
            if (pendingPlan is null)
            {
                // The planners are busy; this bot stands until its next behavior.
                currentPath = null;
            }

            return;
        }

        StraightWalks++;
        currentPath = new RealtimeMovementPath(
            NextPathId(),
            [new RealtimeMovementPathWaypoint(x, position.Y, z)]);
    }

    private void ApplyPlan(Task<MovementPathPlan> completed)
    {
        var plan = completed.IsCompletedSuccessfully ? completed.Result : null;
        if (plan is null || plan.Status == MovementPathPlanStatus.NoPath || plan.Waypoints.Count == 0)
        {
            currentPath = null;
            return;
        }

        var waypoints = new RealtimeMovementPathWaypoint[plan.Waypoints.Count];
        for (var index = 0; index < waypoints.Length; index++)
        {
            var waypoint = plan.Waypoints[index];
            waypoints[index] = new RealtimeMovementPathWaypoint(waypoint.X, waypoint.Y, waypoint.Z);
        }

        PlannedWalks++;
        currentPath = new RealtimeMovementPath(NextPathId(), waypoints);
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

    private double RandomBetween(double minimum, double maximum)
    {
        return minimum + ((maximum - minimum) * random.NextDouble());
    }
}
