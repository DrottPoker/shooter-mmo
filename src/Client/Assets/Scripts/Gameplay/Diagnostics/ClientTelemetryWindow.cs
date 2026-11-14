using System;

namespace Mmorpg.Client.Gameplay
{
    // The frame times and the network rates of the last moments, which the client measurements
    // keep for the Debug menu: the frames of a window of 240, refreshed four times a second, and
    // the packet and byte rates over at least half a second.
    public sealed class ClientTelemetryWindow
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
