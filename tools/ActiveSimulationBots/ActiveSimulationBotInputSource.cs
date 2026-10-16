using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.ActiveSimulationBots;

// Alternates idle intervals with straight walks to random destinations. The worker's
// collision stops a bot at any obstacle, and its path ends once it was blocked.
public sealed class ActiveSimulationBotInputSource : ISimulationBotInputSource
{
    private const double BoundsMargin = 1d;

    // Stays below the worker's 256 meter path limit for any behavior duration.
    private const double MaximumWalkDistance = 200d;

    private readonly ActiveSimulationBotOptions options;
    private readonly Random random;
    private uint nextBehaviorTick;
    private uint lastPathId;
    private RealtimeMovementPath? currentPath;

    public ActiveSimulationBotInputSource(
        ActiveSimulationBotOptions options,
        int botIndex)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        random = new Random(HashCode.Combine(options.Seed, botIndex));
    }

    public RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        if (nextBehaviorTick == 0 || clientTick >= nextBehaviorTick)
        {
            SelectBehavior(clientTick, joinedSession.MovementSettings, position);
        }

        return currentPath;
    }

    private void SelectBehavior(
        uint clientTick,
        RealtimeMovementSettings settings,
        SimulationBotPosition position)
    {
        var durationSeconds = RandomBetween(
            options.MinimumBehaviorDuration.TotalSeconds,
            options.MaximumBehaviorDuration.TotalSeconds);
        if (random.NextDouble() < options.IdleChance)
        {
            currentPath = null;
        }
        else
        {
            // A destination about one behavior interval of walking away, inside the World.
            var angle = random.NextDouble() * Math.PI * 2d;
            var distance = Math.Min(settings.WalkSpeed * durationSeconds, MaximumWalkDistance);
            var x = Math.Clamp(
                position.X + (Math.Sin(angle) * distance),
                settings.MinimumX + BoundsMargin,
                settings.MaximumX - BoundsMargin);
            var z = Math.Clamp(
                position.Z + (Math.Cos(angle) * distance),
                settings.MinimumZ + BoundsMargin,
                settings.MaximumZ - BoundsMargin);
            unchecked
            {
                lastPathId++;
            }

            if (lastPathId == 0)
            {
                lastPathId = 1;
            }

            currentPath = new RealtimeMovementPath(
                lastPathId,
                [new RealtimeMovementPathWaypoint((float)x, position.Y, (float)z)]);
        }

        var durationTicks = Math.Max(1u, (uint)Math.Round(durationSeconds * settings.TickRateHz));
        nextBehaviorTick = clientTick + durationTicks;
    }

    private double RandomBetween(double minimum, double maximum)
    {
        return minimum + ((maximum - minimum) * random.NextDouble());
    }
}
