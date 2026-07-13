using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;

namespace WorldServer.Realtime;

public sealed class AuthoritativePlayerMovement(
    PlayerMovementState initialState,
    MovementSimulationSettings settings,
    ICollisionWorld collisionWorld,
    int maximumInputSilenceTicks)
{
    private readonly int inputSilenceTickLimit = maximumInputSilenceTicks > 0
        ? maximumInputSilenceTicks
        : throw new ArgumentOutOfRangeException(nameof(maximumInputSilenceTicks));
    private PlayerMovementInput latestInput = new(
        0,
        0,
        0f,
        0f,
        initialState.YawDegrees,
        PlayerMovementButtons.None);
    private bool jumpPending;
    private int ticksSinceLatestInput;

    public AuthoritativePlayerMovement(
        PlayerMovementState initialState,
        MovementSimulationSettings settings,
        ICollisionWorld collisionWorld)
        : this(initialState, settings, collisionWorld, Math.Max(1, settings.TickRateHz / 2))
    {
    }

    public PlayerMovementState State { get; private set; } = initialState;

    public uint LastReceivedInputSequence { get; private set; }

    public uint LastProcessedInputSequence { get; private set; }

    public void AcceptInputs(RealtimeMovementInput[] inputs)
    {
        var acceptedInput = false;
        foreach (var input in inputs)
        {
            if (!MovementSequence.IsNewer(input.InputSequence, LastReceivedInputSequence))
            {
                continue;
            }

            var buttons = ToSimulationButtons(input.Buttons);
            jumpPending |= (buttons & PlayerMovementButtons.Jump) != 0;
            latestInput = new PlayerMovementInput(
                input.InputSequence,
                input.ClientTick,
                input.MoveX,
                input.MoveY,
                input.CameraYawDegrees,
                buttons & ~PlayerMovementButtons.Jump);
            LastReceivedInputSequence = input.InputSequence;
            acceptedInput = true;
        }

        if (acceptedInput)
        {
            ticksSinceLatestInput = 0;
        }
    }

    public void SimulateTick()
    {
        var inputIsStale = ticksSinceLatestInput >= inputSilenceTickLimit;
        var buttons = inputIsStale
            ? PlayerMovementButtons.None
            : latestInput.Buttons;
        if (jumpPending)
        {
            buttons |= PlayerMovementButtons.Jump;
        }

        var input = new PlayerMovementInput(
            LastReceivedInputSequence,
            latestInput.ClientTick,
            inputIsStale ? 0f : latestInput.MoveX,
            inputIsStale ? 0f : latestInput.MoveY,
            latestInput.CameraYawDegrees,
            buttons);
        State = PlayerMovementSimulation.Step(State, input, settings, collisionWorld);
        LastProcessedInputSequence = LastReceivedInputSequence;
        jumpPending = false;
        if (ticksSinceLatestInput < int.MaxValue)
        {
            ticksSinceLatestInput++;
        }
    }

    private static PlayerMovementButtons ToSimulationButtons(RealtimeMovementButtons buttons)
    {
        var result = PlayerMovementButtons.None;
        if ((buttons & RealtimeMovementButtons.Sprint) != 0)
        {
            result |= PlayerMovementButtons.Sprint;
        }

        if ((buttons & RealtimeMovementButtons.Jump) != 0)
        {
            result |= PlayerMovementButtons.Jump;
        }

        if ((buttons & RealtimeMovementButtons.Aim) != 0)
        {
            result |= PlayerMovementButtons.Aim;
        }

        return result;
    }
}
