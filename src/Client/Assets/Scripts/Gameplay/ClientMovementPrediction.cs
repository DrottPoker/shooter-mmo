using System.Collections.Generic;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Client.Gameplay
{
    internal readonly struct MovementReconciliation
    {
        public MovementReconciliation(
            PlayerMovementState previousPrediction,
            PlayerMovementState reconciledPrediction)
        {
            PreviousPrediction = previousPrediction;
            ReconciledPrediction = reconciledPrediction;
        }

        public PlayerMovementState PreviousPrediction { get; }

        public PlayerMovementState ReconciledPrediction { get; }
    }

    internal sealed class ClientMovementPrediction
    {
        private const int MaximumPendingInputs = 256;
        private readonly List<PlayerMovementInput> pendingInputs = new List<PlayerMovementInput>();
        private readonly CollisionQueryBuffer collisionQueryBuffer = new CollisionQueryBuffer();
        private readonly MovementSimulationSettings settings;
        private readonly ICollisionWorld collisionWorld;
        private PlayerCarryState carryState;

        public ClientMovementPrediction(
            PlayerMovementState initialState,
            MovementSimulationSettings settings,
            PlayerCarryState carryState,
            ICollisionWorld collisionWorld)
        {
            State = initialState;
            this.settings = settings;
            this.carryState = carryState
                ?? throw new System.ArgumentNullException(nameof(carryState));
            this.collisionWorld = collisionWorld;
        }

        public PlayerMovementState State { get; private set; }

        public int PendingInputCount
        {
            get { return pendingInputs.Count; }
        }

        public PlayerCarryState CarryState
        {
            get { return carryState; }
        }

        // The path id of the newest authoritative state. Until it matches the path of the
        // newest input, every batch carries that path.
        public uint ConfirmedPathId { get; private set; }

        public void ApplyCarryState(PlayerCarryState candidate)
        {
            if (candidate == null)
            {
                throw new System.ArgumentNullException(nameof(candidate));
            }

            if (candidate.ItemStateRevision < carryState.ItemStateRevision)
            {
                throw new System.InvalidOperationException(
                    "Client prediction cannot apply an older carry-state revision.");
            }

            if (candidate.ItemStateRevision == carryState.ItemStateRevision
                && !candidate.Equals(carryState))
            {
                throw new System.InvalidOperationException(
                    "Client prediction cannot apply conflicting carry-state values.");
            }

            carryState = candidate;
        }

        public void Predict(PlayerMovementInput input)
        {
            State = PlayerMovementSimulation.Step(
                State,
                input,
                settings,
                carryState,
                collisionWorld,
                collisionQueryBuffer);
            pendingInputs.Add(input);
            if (pendingInputs.Count > MaximumPendingInputs)
            {
                pendingInputs.RemoveAt(0);
            }
        }

        public MovementReconciliation Reconcile(
            PlayerMovementState authoritativeState,
            uint lastProcessedInputSequence)
        {
            var previousPrediction = State;
            ConfirmedPathId = authoritativeState.PathProgress.PathId;
            pendingInputs.RemoveAll(input =>
                !MovementSequence.IsNewer(input.InputSequence, lastProcessedInputSequence));

            var replayed = authoritativeState;
            for (var index = 0; index < pendingInputs.Count; index++)
            {
                replayed = PlayerMovementSimulation.Step(
                    replayed,
                    pendingInputs[index],
                    settings,
                    carryState,
                    collisionWorld,
                    collisionQueryBuffer);
            }

            State = replayed;
            return new MovementReconciliation(previousPrediction, replayed);
        }

        public PlayerMovementInput[] CreateRedundantInputBatch(out MovementPath unconfirmedPath)
        {
            unconfirmedPath = null;
            if (pendingInputs.Count > 0)
            {
                var newestPath = pendingInputs[pendingInputs.Count - 1].Path;
                if (newestPath != null && newestPath.Id != ConfirmedPathId)
                {
                    unconfirmedPath = newestPath;
                }
            }

            var count = pendingInputs.Count < RealtimeProtocol.MaximumInputBatchSize
                ? pendingInputs.Count
                : RealtimeProtocol.MaximumInputBatchSize;
            var batch = new PlayerMovementInput[count];
            var start = pendingInputs.Count - count;
            for (var index = 0; index < count; index++)
            {
                batch[index] = pendingInputs[start + index];
            }

            return batch;
        }
    }
}
