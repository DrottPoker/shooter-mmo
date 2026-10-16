using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.SimulationWorker.Realtime;

public enum MovementInputAcceptance
{
    Accepted,

    // The path broke the worker's limits, so the character stands at the end of it.
    PathRejected,

    // The newest input names a path that never arrived, which a correct client never does.
    UnknownPath
}

// Follows the client's movement paths with the shared simulation. The worker steps once
// per tick with the newest input, so collision, speed, and bounds stay authoritative.
public sealed class AuthoritativePlayerMovement(
    PlayerMovementState initialState,
    MovementSimulationSettings settings,
    PlayerCarryState initialCarryState,
    ICollisionWorld collisionWorld)
{
    private readonly CollisionQueryBuffer collisionQueryBuffer = new();
    private PlayerMovementInput latestInput = new(0, 0, null);

    public PlayerMovementState State { get; private set; } = initialState;

    public PlayerCarryState CarryState { get; private set; } = initialCarryState
        ?? throw new ArgumentNullException(nameof(initialCarryState));

    public uint LastReceivedInputSequence { get; private set; }

    public uint LastProcessedInputSequence { get; private set; }

    public MovementPath? CurrentPath => latestInput.Path;

    public bool ApplyCarryState(PlayerCarryState carryState)
    {
        ArgumentNullException.ThrowIfNull(carryState);
        if (carryState.ItemStateRevision < CarryState.ItemStateRevision)
        {
            throw new InvalidOperationException(
                "Authoritative movement cannot apply an older carry-state revision.");
        }

        if (carryState.ItemStateRevision == CarryState.ItemStateRevision)
        {
            if (!carryState.Equals(CarryState))
            {
                throw new InvalidOperationException(
                    "One carry-state revision cannot contain conflicting values.");
            }

            return false;
        }

        CarryState = carryState;
        return true;
    }

    // A reconnecting client starts new input sequences and path ids, so the reused entity
    // accepts them from the beginning and stops following the previous client's path.
    public void ResetInputSequence()
    {
        LastReceivedInputSequence = 0;
        LastProcessedInputSequence = 0;
        latestInput = new PlayerMovementInput(0, 0, null);
        State = State.WithPathProgress(MovementPathProgress.None);
    }

    public MovementInputAcceptance AcceptInputs(RealtimeMovementInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        RealtimeMovementInput? newest = null;
        var newestSequence = LastReceivedInputSequence;
        foreach (var input in batch.Inputs)
        {
            if (MovementSequence.IsNewer(input.InputSequence, newestSequence))
            {
                newest = input;
                newestSequence = input.InputSequence;
            }
        }

        if (newest is null)
        {
            return MovementInputAcceptance.Accepted;
        }

        var acceptance = MovementInputAcceptance.Accepted;
        MovementPath? path = null;
        if (newest.PathId != 0)
        {
            if (latestInput.Path?.Id == newest.PathId)
            {
                path = latestInput.Path;
            }
            else if (batch.Path is { } wirePath && wirePath.PathId == newest.PathId)
            {
                path = ToSimulationPath(wirePath);
                if (!MovementPathRules.IsValid(path, State.Position, settings, out _))
                {
                    // Progress at the end makes client and worker agree that the character
                    // stands, and later inputs that still name this path stay valid.
                    State = State.WithPathProgress(
                        new MovementPathProgress(path.Id, path.Count, 0));
                    acceptance = MovementInputAcceptance.PathRejected;
                }
            }
            else
            {
                return MovementInputAcceptance.UnknownPath;
            }
        }

        latestInput = new PlayerMovementInput(newest.InputSequence, newest.ClientTick, path);
        LastReceivedInputSequence = newest.InputSequence;
        return acceptance;
    }

    public void SimulateTick()
    {
        State = PlayerMovementSimulation.Step(
            State,
            latestInput,
            settings,
            CarryState,
            collisionWorld,
            collisionQueryBuffer);
        LastProcessedInputSequence = LastReceivedInputSequence;
    }

    private static MovementPath ToSimulationPath(RealtimeMovementPath wirePath)
    {
        var waypoints = new SimulationVector3[wirePath.Waypoints.Length];
        for (var index = 0; index < waypoints.Length; index++)
        {
            var waypoint = wirePath.Waypoints[index];
            waypoints[index] = new SimulationVector3(waypoint.X, waypoint.Y, waypoint.Z);
        }

        return new MovementPath(wirePath.PathId, waypoints);
    }
}
