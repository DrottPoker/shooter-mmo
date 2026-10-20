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

// Follows the client's movement paths with the shared simulation. The client predicts one
// step per input, so the worker steps exactly once per input too, in sequence order. Inputs
// wait in a short queue that absorbs uneven packet timing, and each tick applies the oldest
// one, or two while a backlog drains. When the next input is late, the tick still steps
// along the current path, at most MaximumRunAheadInputs times in a row, and a longer silence
// pauses the character until inputs arrive; the paused ticks let the queue catch up. A late
// input then takes over the step taken ahead for it, or replaces it when it names another
// path. Stepping per tick with the newest input instead made the worker take more or fewer
// steps than the client whenever packets arrived unevenly, which corrected the local player
// back and forth by whole steps. A step budget keeps the average at one step per tick, with
// 1/64 of slack for a client clock that runs slightly fast, so a client cannot move faster by
// sending more inputs.
public sealed class AuthoritativePlayerMovement
{
    // Steps the worker may take ahead of late inputs, about 100 ms at 30 Hz.
    public const int MaximumRunAheadInputs = 3;

    // Steps a tick may take while a backlog drains.
    public const int MaximumStepsPerTick = 2;

    // A tick takes a second step while more than this many inputs still wait after its first.
    public const int BacklogThreshold = 1;

    // Steps the budget saves while the character pauses, about 330 ms of catch-up at 30 Hz.
    public const int MaximumStepBudget = 10;

    // Inputs a client may have waiting, about 500 ms; a client that floods inputs loses the
    // oldest.
    public const int MaximumQueuedInputs = 16;

    // Lost inputs up to this many are filled with the path before them; a longer gap skips
    // ahead to the received input.
    public const int MaximumFilledInputGap = MaximumQueuedInputs - 1;

    private const int BudgetUnitsPerStep = 64;
    private const int BudgetUnitsPerTick = BudgetUnitsPerStep + 1;
    private const int MaximumBudgetUnits = MaximumStepBudget * BudgetUnitsPerStep;

    private readonly MovementSimulationSettings settings;
    private readonly ICollisionWorld collisionWorld;
    private readonly CollisionQueryBuffer collisionQueryBuffer = new();
    private readonly Queue<QueuedInput> queuedInputs = new();
    private readonly PlayerMovementState[] runAheadStates = new PlayerMovementState[MaximumRunAheadInputs];
    private readonly RealtimeMovementInput[] orderedBatch =
        new RealtimeMovementInput[RealtimeProtocol.MaximumInputBatchSize];
    private PlayerMovementInput currentInput = new(0, 0, null);
    private PlayerMovementInput newestInput = new(0, 0, null);
    private bool hasReceivedInput;
    private bool holdingUntilNextInput;
    private int stepBudgetUnits = MaximumBudgetUnits;
    private int runAheadInputs;

    public AuthoritativePlayerMovement(
        PlayerMovementState initialState,
        MovementSimulationSettings settings,
        PlayerCarryState initialCarryState,
        ICollisionWorld collisionWorld)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.collisionWorld = collisionWorld ?? throw new ArgumentNullException(nameof(collisionWorld));
        CarryState = initialCarryState ?? throw new ArgumentNullException(nameof(initialCarryState));
        State = initialState;
        ConfirmedState = initialState;
    }

    // Where the character is now, including steps taken ahead of late inputs. Collision,
    // combat, interest, and other players use it.
    public PlayerMovementState State { get; private set; }

    // The state after the last processed input. Owner snapshots carry it, so the client
    // replays exactly the inputs after LastProcessedInputSequence.
    public PlayerMovementState ConfirmedState { get; private set; }

    public PlayerCarryState CarryState { get; private set; }

    public uint LastReceivedInputSequence { get; private set; }

    public uint LastProcessedInputSequence { get; private set; }

    public int QueuedInputCount => queuedInputs.Count;

    public int RunAheadInputCount => runAheadInputs;

    // The path the character follows now.
    public MovementPath? CurrentPath => currentInput.Path;

    // No path remains to follow, no waiting input starts one, and no horizontal velocity is
    // left after this tick's movement, which every attack requires.
    public bool IsStandingStill
    {
        get
        {
            return !IsFollowing(currentInput.Path)
                && !HasWaitingMovement()
                && (State.VelocityX * State.VelocityX) + (State.VelocityZ * State.VelocityZ) < 0.0001f;
        }
    }

    // Stops following every received path. The newest one counts as ended, so later inputs
    // that still name it keep the character standing, and the character keeps its physics
    // without waiting for inputs until the next one arrives.
    public void Halt()
    {
        DiscardWaitingInputs();
        if (currentInput.Path is { } path)
        {
            State = State.WithPathProgress(new MovementPathProgress(path.Id, path.Count, 0));
        }

        ConfirmedState = State;
    }

    // Moves the character to a new state, such as its respawn point, standing still.
    public void Teleport(PlayerMovementState state)
    {
        DiscardWaitingInputs();
        State = currentInput.Path is { } path
            ? state.WithPathProgress(new MovementPathProgress(path.Id, path.Count, 0))
            : state.WithPathProgress(MovementPathProgress.None);
        ConfirmedState = State;
    }

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
        currentInput = new PlayerMovementInput(0, 0, null);
        newestInput = currentInput;
        queuedInputs.Clear();
        hasReceivedInput = false;
        holdingUntilNextInput = false;
        stepBudgetUnits = MaximumBudgetUnits;
        runAheadInputs = 0;
        State = State.WithPathProgress(MovementPathProgress.None);
        ConfirmedState = State;
    }

    public MovementInputAcceptance AcceptInputs(RealtimeMovementInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var count = OrderNewInputs(batch.Inputs);
        if (count == 0)
        {
            return MovementInputAcceptance.Accepted;
        }

        // A path that never arrived is checked before anything changes.
        var newest = orderedBatch[count - 1];
        if (newest.PathId != 0
            && newest.PathId != newestInput.PathId
            && newest.PathId != currentInput.PathId
            && batch.Path?.PathId != newest.PathId)
        {
            return MovementInputAcceptance.UnknownPath;
        }

        var acceptance = MovementInputAcceptance.Accepted;
        MovementPath? wirePath = null;
        var wirePathRejected = false;
        if (batch.Path is { } candidate
            && candidate.PathId != newestInput.PathId
            && candidate.PathId != currentInput.PathId)
        {
            wirePath = ToSimulationPath(candidate);
            if (!MovementPathRules.IsValid(wirePath, State.Position, settings, out _))
            {
                // Adopted as already ended when it applies, so client and worker agree that
                // the character stands, and later inputs that name it stay valid.
                wirePathRejected = true;
                acceptance = MovementInputAcceptance.PathRejected;
            }
        }

        for (var index = 0; index < count; index++)
        {
            var input = orderedBatch[index];
            var rejected = false;
            MovementPath? path;
            if (input.PathId == 0)
            {
                path = null;
            }
            else if (input.PathId == newestInput.PathId)
            {
                path = newestInput.Path;
            }
            else if (input.PathId == currentInput.PathId)
            {
                path = currentInput.Path;
            }
            else if (wirePath is not null && input.PathId == wirePath.Id)
            {
                path = wirePath;
                rejected = wirePathRejected;
            }
            else
            {
                // An older input names a path whose waypoints were lost; its step follows the
                // path before it, as a lost input does.
                path = newestInput.Path;
            }

            Receive(new QueuedInput(
                new PlayerMovementInput(input.InputSequence, input.ClientTick, path),
                rejected));
        }

        holdingUntilNextInput = false;
        return acceptance;
    }

    public void SimulateTick()
    {
        stepBudgetUnits = Math.Min(MaximumBudgetUnits, stepBudgetUnits + BudgetUnitsPerTick);
        if (holdingUntilNextInput)
        {
            // A halted or teleported character keeps its physics while no input arrives.
            Step(currentInput);
            ConfirmedState = State;
            return;
        }

        if (queuedInputs.Count > 0)
        {
            var steps = 0;
            while (queuedInputs.Count > 0
                && stepBudgetUnits >= BudgetUnitsPerStep
                && (steps == 0
                    || (steps < MaximumStepsPerTick && queuedInputs.Count > BacklogThreshold)))
            {
                var queued = queuedInputs.Dequeue();
                Apply(queued);
                Step(currentInput);
                ConfirmedState = State;
                LastProcessedInputSequence = queued.Input.InputSequence;
                stepBudgetUnits -= BudgetUnitsPerStep;
                steps++;
            }

            return;
        }

        if (hasReceivedInput
            && runAheadInputs < MaximumRunAheadInputs
            && stepBudgetUnits >= BudgetUnitsPerStep)
        {
            Step(currentInput);
            runAheadStates[runAheadInputs] = State;
            runAheadInputs++;
            stepBudgetUnits -= BudgetUnitsPerStep;
        }
    }

    // Receives one input newer than every received one, in sequence order.
    private void Receive(QueuedInput queued)
    {
        var sequence = queued.Input.InputSequence;
        if (!hasReceivedInput)
        {
            hasReceivedInput = true;
            LastProcessedInputSequence = unchecked(sequence - 1);
        }
        else
        {
            var gap = unchecked(sequence - LastReceivedInputSequence - 1);
            if (gap > MaximumFilledInputGap)
            {
                // Too much was lost to replay, so the worker continues from this input.
                queuedInputs.Clear();
                runAheadInputs = 0;
                ConfirmedState = State;
                LastProcessedInputSequence = unchecked(sequence - 1);
            }
            else
            {
                for (var missing = 1u; missing <= gap; missing++)
                {
                    Process(new QueuedInput(
                        new PlayerMovementInput(
                            unchecked(LastReceivedInputSequence + missing),
                            unchecked(newestInput.ClientTick + missing),
                            newestInput.Path),
                        false));
                }
            }
        }

        Process(queued);
        LastReceivedInputSequence = sequence;
        newestInput = queued.Input;
    }

    // The input after LastProcessedInputSequence either takes over the oldest step taken
    // ahead for it or waits in the queue.
    private void Process(QueuedInput queued)
    {
        if (runAheadInputs == 0)
        {
            if (queuedInputs.Count == MaximumQueuedInputs)
            {
                queuedInputs.Dequeue();
            }

            queuedInputs.Enqueue(queued);
            return;
        }

        LastProcessedInputSequence = queued.Input.InputSequence;
        if (queued.Input.PathId == currentInput.PathId)
        {
            // The step taken ahead followed this input's path, so it stands as it is.
            ConfirmedState = runAheadStates[0];
            Array.Copy(runAheadStates, 1, runAheadStates, 0, runAheadInputs - 1);
            runAheadInputs--;
            return;
        }

        // The late input changed the path: the steps taken ahead are taken again with it.
        var stepsAhead = runAheadInputs;
        State = ConfirmedState;
        Apply(queued);
        Step(currentInput);
        ConfirmedState = State;
        runAheadInputs = 0;
        for (var index = 1; index < stepsAhead; index++)
        {
            Step(currentInput);
            runAheadStates[runAheadInputs] = State;
            runAheadInputs++;
        }
    }

    private void Apply(QueuedInput queued)
    {
        if (queued.PathRejected
            && queued.Input.Path is { } path
            && path.Id != currentInput.PathId)
        {
            State = State.WithPathProgress(new MovementPathProgress(path.Id, path.Count, 0));
        }

        currentInput = queued.Input;
    }

    private void Step(PlayerMovementInput input)
    {
        State = PlayerMovementSimulation.Step(
            State,
            input,
            settings,
            CarryState,
            collisionWorld,
            collisionQueryBuffer);
    }

    // Everything received counts as processed, and the character stands where it is now.
    private void DiscardWaitingInputs()
    {
        queuedInputs.Clear();
        runAheadInputs = 0;
        currentInput = newestInput;
        LastProcessedInputSequence = LastReceivedInputSequence;
        holdingUntilNextInput = true;
    }

    // Any waiting input may switch to another path, even when a later one stops again.
    private bool HasWaitingMovement()
    {
        foreach (var queued in queuedInputs)
        {
            if (!queued.PathRejected
                && queued.Input.PathId != currentInput.PathId
                && IsFollowing(queued.Input.Path))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsFollowing(MovementPath? path)
    {
        return path is not null
            && !(State.PathProgress.PathId == path.Id
                && State.PathProgress.NextWaypointIndex >= path.Count);
    }

    // Copies the inputs newer than every received one into orderedBatch, oldest first.
    private int OrderNewInputs(RealtimeMovementInput[] inputs)
    {
        var count = 0;
        foreach (var input in inputs)
        {
            if (count == orderedBatch.Length
                || (hasReceivedInput
                    && !MovementSequence.IsNewer(input.InputSequence, LastReceivedInputSequence)))
            {
                continue;
            }

            var position = count;
            while (position > 0
                && MovementSequence.IsNewer(orderedBatch[position - 1].InputSequence, input.InputSequence))
            {
                orderedBatch[position] = orderedBatch[position - 1];
                position--;
            }

            if (position > 0 && orderedBatch[position - 1].InputSequence == input.InputSequence)
            {
                // A sequence that appears twice in one batch counts once.
                Array.Copy(orderedBatch, position + 1, orderedBatch, position, count - position);
                continue;
            }

            orderedBatch[position] = input;
            count++;
        }

        return count;
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

    private readonly record struct QueuedInput(PlayerMovementInput Input, bool PathRejected);
}
