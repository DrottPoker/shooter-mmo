using ShooterMmo.GameProtocol;
using ShooterMmo.Tools.SimulationBotClient;

namespace ShooterMmo.Tools.ActiveSimulationBots;

public sealed class ActiveSimulationBotInputSource : ISimulationBotInputSource
{
    private readonly ActiveSimulationBotOptions options;
    private readonly Random random;
    private uint nextBehaviorTick;
    private float moveX;
    private float moveY;
    private float yaw;
    private RealtimeMovementButtons persistentButtons;

    public ActiveSimulationBotInputSource(
        ActiveSimulationBotOptions options,
        int botIndex)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        random = new Random(HashCode.Combine(options.Seed, botIndex));
    }

    public RealtimeMovementInput CreateInput(
        uint inputSequence,
        uint clientTick,
        RealtimeJoinAccepted joinedSession)
    {
        if (nextBehaviorTick == 0 || clientTick >= nextBehaviorTick)
        {
            SelectBehavior(clientTick, joinedSession.MovementSettings.TickRateHz);
        }

        var buttons = persistentButtons;
        var jumpChanceThisTick = options.JumpChancePerSecond
            / joinedSession.MovementSettings.TickRateHz;
        if ((buttons & RealtimeMovementButtons.Aim) == 0
            && random.NextDouble() < jumpChanceThisTick)
        {
            buttons |= RealtimeMovementButtons.Jump;
        }

        return new RealtimeMovementInput(
            inputSequence,
            clientTick,
            moveX,
            moveY,
            yaw,
            buttons);
    }

    private void SelectBehavior(uint clientTick, uint tickRateHz)
    {
        var idle = random.NextDouble() < options.IdleChance;
        if (idle)
        {
            moveX = 0f;
            moveY = 0f;
        }
        else
        {
            var movementAngle = random.NextDouble() * Math.PI * 2d;
            moveX = (float)Math.Sin(movementAngle);
            moveY = (float)Math.Cos(movementAngle);
        }

        yaw = (float)(random.NextDouble() * 360d);
        var aiming = random.NextDouble() < options.AimChance;
        persistentButtons = aiming
            ? RealtimeMovementButtons.Aim
            : random.NextDouble() < options.SprintChance
                ? RealtimeMovementButtons.Sprint
                : RealtimeMovementButtons.None;

        var durationSeconds = RandomBetween(
            options.MinimumDirectionDuration.TotalSeconds,
            options.MaximumDirectionDuration.TotalSeconds);
        var durationTicks = Math.Max(1u, (uint)Math.Round(durationSeconds * tickRateHz));
        nextBehaviorTick = clientTick + durationTicks;
    }

    private double RandomBetween(double minimum, double maximum)
    {
        return minimum + ((maximum - minimum) * random.NextDouble());
    }
}
