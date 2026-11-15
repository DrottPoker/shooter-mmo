using System;

namespace Mmorpg.GameProtocol
{
    // The presentation archetypes of player entities in EntitySpawn.ArchetypeId. The archetype
    // is free text on the wire, so these values are a contract between SimulationWorker and the
    // client that changes neither the wire format nor the protocol version.
    public static class RealtimePlayerArchetypeIds
    {
        // A character that a person plays.
        public const string Player = "player.default";

        // A synthetic development bot from tools/ActiveSimulationBots, presented so players can
        // tell it from a person.
        public const string DevelopmentBot = "player.development_bot";
    }

    // The precision of a remote entity record: x and z in steps of 1/128 m as 24-bit integers,
    // y in steps of 1/64 m as a 16-bit integer, and the yaw in 1024 steps. The worker rounds to
    // the nearest step and clamps to the range, and a client multiplies back. These numbers are
    // part of the wire contract that the version lock cannot see, so a change needs a new
    // protocol version by hand.
    public sealed partial class RealtimeEntitySnapshot
    {
        public const int HorizontalStepsPerMeter = 128;

        public const int VerticalStepsPerMeter = 64;

        public const int YawSteps = 1024;

        // The whole meters that x and z, and y, reach without clamping, in both directions.
        public const int MaximumHorizontalMeters = 65535;

        public const int MaximumVerticalMeters = 511;

        private const int MinimumHorizontalStep = -(1 << 23);
        private const int MaximumHorizontalStep = (1 << 23) - 1;

        // World position on the x axis in meters.
        public float PositionX
        {
            get { return QuantizedX / (float)HorizontalStepsPerMeter; }
        }

        // World position on the y axis in meters.
        public float PositionY
        {
            get { return QuantizedY / (float)VerticalStepsPerMeter; }
        }

        // World position on the z axis in meters.
        public float PositionZ
        {
            get { return QuantizedZ / (float)HorizontalStepsPerMeter; }
        }

        // Facing around the vertical axis in degrees, from 0 up to 360.
        public float YawDegrees
        {
            get { return YawStep * (360f / YawSteps); }
        }

        // The record of a state, rounded to the nearest step. Throws ArgumentException for a
        // value that is not finite.
        public static RealtimeEntitySnapshot Quantize(
            uint entityId,
            float positionX,
            float positionY,
            float positionZ,
            float yawDegrees,
            bool isGrounded)
        {
            if (!TryQuantizeHorizontal(positionX, out var quantizedX)
                || !TryQuantizeVertical(positionY, out var quantizedY)
                || !TryQuantizeHorizontal(positionZ, out var quantizedZ)
                || !TryQuantizeYaw(yawDegrees, out var yawStep))
            {
                throw new ArgumentException("An entity snapshot needs finite values.");
            }

            return new RealtimeEntitySnapshot(entityId, quantizedX, quantizedY, quantizedZ, yawStep, isGrounded);
        }

        // Meters on x or z to the nearest 1/128 m step, clamped to the 24-bit range. False for a
        // value that is not finite.
        public static bool TryQuantizeHorizontal(float meters, out int steps)
        {
            if (!IsFinite(meters))
            {
                steps = 0;
                return false;
            }

            steps = (int)Clamp(
                Math.Round((double)meters * HorizontalStepsPerMeter, MidpointRounding.AwayFromZero),
                MinimumHorizontalStep,
                MaximumHorizontalStep);
            return true;
        }

        // Meters on y to the nearest 1/64 m step, clamped to the 16-bit range.
        public static bool TryQuantizeVertical(float meters, out short steps)
        {
            if (!IsFinite(meters))
            {
                steps = 0;
                return false;
            }

            steps = (short)Clamp(
                Math.Round((double)meters * VerticalStepsPerMeter, MidpointRounding.AwayFromZero),
                short.MinValue,
                short.MaxValue);
            return true;
        }

        // Degrees in any turn to the nearest of 1024 steps of a turn.
        public static bool TryQuantizeYaw(float degrees, out ushort step)
        {
            if (!IsFinite(degrees))
            {
                step = 0;
                return false;
            }

            var turns = (double)degrees / 360d;
            var steps = Math.Round((turns - Math.Floor(turns)) * YawSteps, MidpointRounding.AwayFromZero);
            step = (ushort)((long)steps % YawSteps);
            return true;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
