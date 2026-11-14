using System;
using System.Globalization;

namespace Mmorpg.Client.Ui
{
    public enum WorldDebugHealth
    {
        Neutral,
        Healthy,
        Warning,
        Critical,
        Unavailable
    }

    public static class WorldDebugDiagnostics
    {
        public static WorldDebugHealth EvaluateFrame(
            float p95Milliseconds,
            float budgetMilliseconds)
        {
            if (p95Milliseconds <= 0f || budgetMilliseconds <= 0f)
            {
                return WorldDebugHealth.Unavailable;
            }

            if (p95Milliseconds <= budgetMilliseconds)
            {
                return WorldDebugHealth.Healthy;
            }

            return p95Milliseconds <= budgetMilliseconds * 1.5f
                ? WorldDebugHealth.Warning
                : WorldDebugHealth.Critical;
        }

        public static WorldDebugHealth EvaluatePing(int milliseconds)
        {
            if (milliseconds < 0)
            {
                return WorldDebugHealth.Unavailable;
            }

            if (milliseconds < 80)
            {
                return WorldDebugHealth.Healthy;
            }

            return milliseconds < 150
                ? WorldDebugHealth.Warning
                : WorldDebugHealth.Critical;
        }

        public static WorldDebugHealth EvaluateSnapshotAge(
            float ageSeconds,
            int expectedRateHz)
        {
            if (ageSeconds < 0f || expectedRateHz <= 0)
            {
                return WorldDebugHealth.Unavailable;
            }

            var expectedInterval = 1f / expectedRateHz;
            if (ageSeconds <= expectedInterval * 3f)
            {
                return WorldDebugHealth.Healthy;
            }

            return ageSeconds <= expectedInterval * 8f
                ? WorldDebugHealth.Warning
                : WorldDebugHealth.Critical;
        }

        public static WorldDebugHealth EvaluateLoss(float percent)
        {
            if (percent < 0f)
            {
                return WorldDebugHealth.Unavailable;
            }

            if (percent < 1f)
            {
                return WorldDebugHealth.Healthy;
            }

            return percent < 5f
                ? WorldDebugHealth.Warning
                : WorldDebugHealth.Critical;
        }

        public static WorldDebugHealth EvaluatePendingInputs(int count)
        {
            if (count < 0)
            {
                return WorldDebugHealth.Unavailable;
            }

            if (count <= 8)
            {
                return WorldDebugHealth.Healthy;
            }

            return count <= 24
                ? WorldDebugHealth.Warning
                : WorldDebugHealth.Critical;
        }

        public static string Milliseconds(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }

        public static string Megabytes(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        }

        public static string Rate(float value, string unit)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
        }

        public static string Percent(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        public static string Age(float value)
        {
            return value < 0f
                ? "waiting"
                : value.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        public static string ShortIdentifier(string value, int maximumLength = 16)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maximumLength) + "...";
        }
    }
}
