using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
{
    internal sealed class LocalMovementPresentation
    {
        private PlayerMovementState interpolationStart;
        private PlayerMovementState interpolationTarget;
        private float interpolationElapsed;
        private bool isInitialized;

        public PlayerMovementState State { get; private set; }

        public void Reset(PlayerMovementState state)
        {
            interpolationStart = state;
            interpolationTarget = state;
            interpolationElapsed = 0f;
            State = state;
            isInitialized = true;
        }

        public void Retarget(PlayerMovementState state)
        {
            if (!isInitialized)
            {
                Reset(state);
                return;
            }

            interpolationStart = State;
            interpolationTarget = state;
            interpolationElapsed = 0f;
        }

        public void Advance(float deltaTime, float tickDuration)
        {
            if (!isInitialized)
            {
                return;
            }

            if (tickDuration <= 0f)
            {
                State = interpolationTarget;
                return;
            }

            interpolationElapsed += deltaTime > 0f ? deltaTime : 0f;
            var amount = interpolationElapsed / tickDuration;
            State = MovementStateInterpolation.Interpolate(
                interpolationStart,
                interpolationTarget,
                amount);
        }

        public void ApplySimulationCorrection(PlayerMovementState correctedTarget)
        {
            if (!isInitialized)
            {
                Reset(correctedTarget);
                return;
            }

            var positionX = correctedTarget.PositionX - interpolationTarget.PositionX;
            var positionY = correctedTarget.PositionY - interpolationTarget.PositionY;
            var positionZ = correctedTarget.PositionZ - interpolationTarget.PositionZ;
            var yawDegrees = MovementStateInterpolation.DeltaAngle(
                interpolationTarget.YawDegrees,
                correctedTarget.YawDegrees);

            interpolationStart = MovementStateInterpolation.ShiftPose(
                interpolationStart,
                positionX,
                positionY,
                positionZ,
                yawDegrees);
            State = MovementStateInterpolation.ShiftPose(
                State,
                positionX,
                positionY,
                positionZ,
                yawDegrees);
            interpolationTarget = correctedTarget;
        }
    }
}
