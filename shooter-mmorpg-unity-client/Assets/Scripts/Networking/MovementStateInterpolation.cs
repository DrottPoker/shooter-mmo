using System;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Networking
{
    internal static class MovementStateInterpolation
    {
        public static PlayerMovementState Interpolate(
            PlayerMovementState from,
            PlayerMovementState to,
            float amount)
        {
            amount = Clamp01(amount);
            return new PlayerMovementState(
                Lerp(from.PositionX, to.PositionX, amount),
                Lerp(from.PositionY, to.PositionY, amount),
                Lerp(from.PositionZ, to.PositionZ, amount),
                Lerp(from.VelocityX, to.VelocityX, amount),
                Lerp(from.VelocityY, to.VelocityY, amount),
                Lerp(from.VelocityZ, to.VelocityZ, amount),
                LerpAngle(from.YawDegrees, to.YawDegrees, amount),
                amount < 0.5f ? from.IsGrounded : to.IsGrounded,
                amount < 0.5f ? from.IsSprinting : to.IsSprinting);
        }

        public static PlayerMovementState ShiftPose(
            PlayerMovementState state,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees)
        {
            return new PlayerMovementState(
                state.PositionX + positionX,
                state.PositionY + positionY,
                state.PositionZ + positionZ,
                state.VelocityX,
                state.VelocityY,
                state.VelocityZ,
                NormalizeAngle(state.YawDegrees + yawDegrees),
                state.IsGrounded,
                state.IsSprinting);
        }

        public static float DeltaAngle(float from, float to)
        {
            return ((to - from + 540f) % 360f) - 180f;
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + ((to - from) * amount);
        }

        private static float LerpAngle(float from, float to, float amount)
        {
            return NormalizeAngle(from + (DeltaAngle(from, to) * amount));
        }

        private static float NormalizeAngle(float angle)
        {
            var result = angle % 360f;
            return result < 0f ? result + 360f : result;
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
