using System;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
{
    internal static class GroundedMovementPresentation
    {
        public const float StepSmoothingDurationSeconds = 0.1f;
        private const float FlatGroundNormalY = 0.999f;

        public static bool TryGetVisualHeight(
            float positionX,
            float positionY,
            float positionZ,
            bool isGrounded,
            ICollisionWorld collisionWorld,
            CharacterCollisionSettings collisionSettings,
            CollisionQueryBuffer queryBuffer,
            out float visualHeight,
            out SimulationVector3 groundNormal)
        {
            visualHeight = positionY;
            groundNormal = SimulationVector3.Zero;
            if (!isGrounded
                || collisionWorld == null
                || collisionSettings == null
                || queryBuffer == null)
            {
                return false;
            }

            if (!KinematicCharacterMotor.TryFindWalkableGroundSurface(
                    collisionWorld,
                    collisionSettings,
                    new SimulationVector3(positionX, positionY, positionZ),
                    0f,
                    collisionSettings.GroundSnapDistance + collisionSettings.Radius,
                    queryBuffer,
                    out _,
                    out groundNormal))
            {
                return false;
            }

            var capsuleSupportOffset = KinematicCharacterMotor.CalculateCapsuleSupportHeight(
                0f,
                groundNormal,
                collisionSettings.Radius);
            visualHeight = positionY - capsuleSupportOffset;
            return true;
        }

        public static bool IsFlatGround(SimulationVector3 groundNormal)
        {
            return groundNormal.Y >= FlatGroundNormalY;
        }
    }

    internal sealed class GroundedVerticalPresentation
    {
        private const double CompletionSharpness = 4.605170185988092d;
        private const float CompletionEpsilon = 0.0001f;

        private bool isInitialized;
        private float currentHeight;

        public float CurrentHeight
        {
            get { return currentHeight; }
        }

        public void Clear()
        {
            isInitialized = false;
            currentHeight = 0f;
        }

        public void Reset(float height)
        {
            currentHeight = height;
            isInitialized = true;
        }

        public float Update(
            float targetHeight,
            bool shouldSmooth,
            float maximumSmoothDistance,
            float smoothingDuration,
            float deltaTime)
        {
            if (!isInitialized)
            {
                Reset(targetHeight);
                return currentHeight;
            }

            var difference = targetHeight - currentHeight;
            if (!shouldSmooth
                || smoothingDuration <= 0f
                || maximumSmoothDistance <= 0f
                || Math.Abs(difference) > maximumSmoothDistance)
            {
                Reset(targetHeight);
                return currentHeight;
            }

            var safeDeltaTime = Math.Max(0f, deltaTime);
            var blend = 1d - Math.Exp(
                -CompletionSharpness * safeDeltaTime / smoothingDuration);
            currentHeight += difference * (float)blend;
            if (Math.Abs(targetHeight - currentHeight) <= CompletionEpsilon)
            {
                currentHeight = targetHeight;
            }

            return currentHeight;
        }
    }
}
