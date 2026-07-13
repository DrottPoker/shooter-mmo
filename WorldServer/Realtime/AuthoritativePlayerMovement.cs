using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;

namespace WorldServer.Realtime;

public sealed class AuthoritativePlayerMovement(
    PlayerMovementState initialState,
    MovementSimulationSettings settings,
    ICollisionWorld collisionWorld)
{
    private PlayerMovementInput latestInput = new(
        0,
        0,
        0f,
        0f,
        initialState.YawDegrees,
        PlayerMovementButtons.None);
    private bool jumpPending;

    public PlayerMovementState State { get; private set; } = initialState;

    public uint LastReceivedInputSequence { get; private set; }

    public uint LastProcessedInputSequence { get; private set; }

    public void AcceptInputs(RealtimeMovementInput[] inputs)
    {
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
        }
    }

    public void SimulateTick()
    {
        var buttons = latestInput.Buttons;
        if (jumpPending)
        {
            buttons |= PlayerMovementButtons.Jump;
        }

        var input = new PlayerMovementInput(
            LastReceivedInputSequence,
            latestInput.ClientTick,
            latestInput.MoveX,
            latestInput.MoveY,
            latestInput.CameraYawDegrees,
            buttons);
        State = PlayerMovementSimulation.Step(State, input, settings, collisionWorld);
        LastProcessedInputSequence = LastReceivedInputSequence;
        jumpPending = false;
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
