using System.Collections.Generic;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
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

        public ClientMovementPrediction(
            PlayerMovementState initialState,
            MovementSimulationSettings settings,
            ICollisionWorld collisionWorld)
        {
            State = initialState;
            this.settings = settings;
            this.collisionWorld = collisionWorld;
        }

        public PlayerMovementState State { get; private set; }

        public int PendingInputCount
        {
            get { return pendingInputs.Count; }
        }

        public void Predict(PlayerMovementInput input)
        {
            State = PlayerMovementSimulation.Step(
                State,
                input,
                settings,
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
            pendingInputs.RemoveAll(input =>
                !MovementSequence.IsNewer(input.InputSequence, lastProcessedInputSequence));

            var replayed = authoritativeState;
            for (var index = 0; index < pendingInputs.Count; index++)
            {
                replayed = PlayerMovementSimulation.Step(
                    replayed,
                    pendingInputs[index],
                    settings,
                    collisionWorld,
                    collisionQueryBuffer);
            }

            State = replayed;
            return new MovementReconciliation(previousPrediction, replayed);
        }

        public PlayerMovementInput[] CreateRedundantInputBatch()
        {
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
