using System;
using System.Globalization;

namespace ShooterMmo.Ui
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

        public static float EstimateSnapshotLossPercent(
            long receivedFrames,
            long estimatedMissingFrames)
        {
            var expectedFrames = receivedFrames + estimatedMissingFrames;
            if (receivedFrames < 0 || estimatedMissingFrames < 0 || expectedFrames <= 0)
            {
                return 0f;
            }

            return (float)(estimatedMissingFrames * 100d / expectedFrames);
        }
    }

    public sealed class WorldDebugTelemetryTracker
    {
        private const int MaximumFrameSamples = 240;
        private const float FrameRefreshIntervalSeconds = 0.25f;
        private const float NetworkRefreshIntervalSeconds = 0.5f;

        private readonly float[] frameMilliseconds = new float[MaximumFrameSamples];
        private readonly float[] sortedFrameMilliseconds = new float[MaximumFrameSamples];
        private int frameSampleCount;
        private int nextFrameSample;
        private float frameRefreshElapsed;
        private bool hasNetworkBaseline;
        private float previousNetworkSampleAt;
        private long previousReceivedPackets;
        private long previousReceivedBytes;
        private long previousSentPackets;
        private long previousSentBytes;
        private long previousSnapshotPackets;
        private long previousSnapshotFrames;

        public float FramesPerSecond { get; private set; }

        public float AverageFrameMilliseconds { get; private set; }

        public float P95FrameMilliseconds { get; private set; }

        public float MaximumFrameMilliseconds { get; private set; }

        public float ReceivedPacketsPerSecond { get; private set; }

        public float SentPacketsPerSecond { get; private set; }

        public float ReceivedKibibytesPerSecond { get; private set; }

        public float SentKibibytesPerSecond { get; private set; }

        public float SnapshotPacketsPerSecond { get; private set; }

        public float SnapshotFramesPerSecond { get; private set; }

        public float NetworkSampleSeconds { get; private set; }

        public void RecordFrame(float unscaledDeltaSeconds)
        {
            if (unscaledDeltaSeconds <= 0f
                || float.IsNaN(unscaledDeltaSeconds)
                || float.IsInfinity(unscaledDeltaSeconds))
            {
                return;
            }

            frameMilliseconds[nextFrameSample] = unscaledDeltaSeconds * 1000f;
            nextFrameSample = (nextFrameSample + 1) % MaximumFrameSamples;
            frameSampleCount = Math.Min(frameSampleCount + 1, MaximumFrameSamples);
            frameRefreshElapsed += unscaledDeltaSeconds;
            if (frameRefreshElapsed < FrameRefreshIntervalSeconds)
            {
                return;
            }

            frameRefreshElapsed = 0f;
            RefreshFrameMetrics();
        }

        public void SampleNetwork(
            float realtimeSeconds,
            long receivedPackets,
            long receivedBytes,
            long sentPackets,
            long sentBytes,
            long snapshotPackets,
            long snapshotFrames)
        {
            var countersMovedBackwards = hasNetworkBaseline
                && CountersMovedBackwards(
                    receivedPackets,
                    receivedBytes,
                    sentPackets,
                    sentBytes,
                    snapshotPackets,
                    snapshotFrames);
            if (!hasNetworkBaseline
                || realtimeSeconds < previousNetworkSampleAt
                || countersMovedBackwards)
            {
                if (hasNetworkBaseline)
                {
                    ClearNetworkRates();
                }

                SetNetworkBaseline(
                    realtimeSeconds,
                    receivedPackets,
                    receivedBytes,
                    sentPackets,
                    sentBytes,
                    snapshotPackets,
                    snapshotFrames);
                return;
            }

            var elapsed = realtimeSeconds - previousNetworkSampleAt;
            if (elapsed < NetworkRefreshIntervalSeconds)
            {
                return;
            }

            NetworkSampleSeconds = elapsed;
            ReceivedPacketsPerSecond = (receivedPackets - previousReceivedPackets) / elapsed;
            SentPacketsPerSecond = (sentPackets - previousSentPackets) / elapsed;
            ReceivedKibibytesPerSecond = (receivedBytes - previousReceivedBytes) / elapsed / 1024f;
            SentKibibytesPerSecond = (sentBytes - previousSentBytes) / elapsed / 1024f;
            SnapshotPacketsPerSecond = (snapshotPackets - previousSnapshotPackets) / elapsed;
            SnapshotFramesPerSecond = (snapshotFrames - previousSnapshotFrames) / elapsed;
            SetNetworkBaseline(
                realtimeSeconds,
                receivedPackets,
                receivedBytes,
                sentPackets,
                sentBytes,
                snapshotPackets,
                snapshotFrames);
        }

        public void ResetNetwork()
        {
            hasNetworkBaseline = false;
            ClearNetworkRates();
        }

        private void ClearNetworkRates()
        {
            NetworkSampleSeconds = 0f;
            ReceivedPacketsPerSecond = 0f;
            SentPacketsPerSecond = 0f;
            ReceivedKibibytesPerSecond = 0f;
            SentKibibytesPerSecond = 0f;
            SnapshotPacketsPerSecond = 0f;
            SnapshotFramesPerSecond = 0f;
        }

        private void RefreshFrameMetrics()
        {
            if (frameSampleCount == 0)
            {
                return;
            }

            var total = 0f;
            var maximum = 0f;
            for (var index = 0; index < frameSampleCount; index++)
            {
                var sample = frameMilliseconds[index];
                sortedFrameMilliseconds[index] = sample;
                total += sample;
                maximum = Math.Max(maximum, sample);
            }

            Array.Sort(sortedFrameMilliseconds, 0, frameSampleCount);
            AverageFrameMilliseconds = total / frameSampleCount;
            FramesPerSecond = AverageFrameMilliseconds <= 0f
                ? 0f
                : 1000f / AverageFrameMilliseconds;
            var percentileIndex = Math.Max(
                0,
                Math.Min(
                    frameSampleCount - 1,
                    (int)Math.Ceiling(frameSampleCount * 0.95f) - 1));
            P95FrameMilliseconds = sortedFrameMilliseconds[percentileIndex];
            MaximumFrameMilliseconds = maximum;
        }

        private bool CountersMovedBackwards(
            long receivedPackets,
            long receivedBytes,
            long sentPackets,
            long sentBytes,
            long snapshotPackets,
            long snapshotFrames)
        {
            return receivedPackets < previousReceivedPackets
                || receivedBytes < previousReceivedBytes
                || sentPackets < previousSentPackets
                || sentBytes < previousSentBytes
                || snapshotPackets < previousSnapshotPackets
                || snapshotFrames < previousSnapshotFrames;
        }

        private void SetNetworkBaseline(
            float realtimeSeconds,
            long receivedPackets,
            long receivedBytes,
            long sentPackets,
            long sentBytes,
            long snapshotPackets,
            long snapshotFrames)
        {
            hasNetworkBaseline = true;
            previousNetworkSampleAt = realtimeSeconds;
            previousReceivedPackets = receivedPackets;
            previousReceivedBytes = receivedBytes;
            previousSentPackets = sentPackets;
            previousSentBytes = sentBytes;
            previousSnapshotPackets = snapshotPackets;
            previousSnapshotFrames = snapshotFrames;
        }
    }
}
