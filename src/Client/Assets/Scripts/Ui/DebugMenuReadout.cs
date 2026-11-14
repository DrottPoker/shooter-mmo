using System.Globalization;
using Mmorpg.Client.Gameplay;

namespace Mmorpg.Client.Ui
{
    // The texts of the Debug menu's numbers, every one read from the client measurements, so the
    // menu shows what the recorder measured and computes nothing of its own.
    public static class DebugMenuReadout
    {
        public static string FrameGlance(ClientMeasurements measurements)
        {
            var telemetry = measurements.Telemetry;
            return WorldDebugDiagnostics.Rate(telemetry.FramesPerSecond, "FPS") + ", P95 "
                + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds);
        }

        public static string FrameTimes(ClientMeasurements measurements)
        {
            var telemetry = measurements.Telemetry;
            return WorldDebugDiagnostics.Milliseconds(telemetry.AverageFrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.P95FrameMilliseconds)
                + " / " + WorldDebugDiagnostics.Milliseconds(telemetry.MaximumFrameMilliseconds);
        }

        public static string RoundTrip(ClientMeasurements measurements)
        {
            return measurements.RoundTripMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        public static string SnapshotAge(ClientMeasurements measurements)
        {
            return WorldDebugDiagnostics.Age(measurements.LatestSnapshotAgeSeconds);
        }

        public static string SnapshotLoss(ClientMeasurements measurements)
        {
            return WorldDebugDiagnostics.Percent(measurements.EstimatedSnapshotLossPercent)
                + " (" + measurements.EstimatedMissingSnapshotFrames.ToString(CultureInfo.InvariantCulture) + " missing)";
        }

        public static string Reconciliations(ClientMeasurements measurements)
        {
            return measurements.Reconciliations.ToString(CultureInfo.InvariantCulture)
                + " / " + measurements.HardSnaps.ToString(CultureInfo.InvariantCulture);
        }

        public static string LatestCorrection(ClientMeasurements measurements)
        {
            return measurements.LatestCorrectionMeters.ToString("0.000", CultureInfo.InvariantCulture) + " m";
        }

        public static string RecordedCorrections(ClientMeasurementRecord summary)
        {
            return Meters(summary.correctionMeters.p95) + " / "
                + summary.hardSnaps.ToString(CultureInfo.InvariantCulture);
        }

        public static string RecordedJumps(ClientMeasurementRecord summary)
        {
            return Meters(summary.largestUnexplainedJumpMeters) + " / "
                + summary.framesWithUnexplainedJump.ToString(CultureInfo.InvariantCulture);
        }

        public static string RecordedRemote(ClientMeasurementRecord summary)
        {
            return WorldDebugDiagnostics.Percent(summary.remoteStarvedPercent) + " / "
                + summary.longestRemoteStarvationSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        public static string RecordedIntents(ClientMeasurementRecord summary)
        {
            return summary.intentsAnswered.ToString(CultureInfo.InvariantCulture) + " of "
                + summary.intentsSent.ToString(CultureInfo.InvariantCulture) + " / "
                + (summary.intentSeconds.p95 * 1000f).ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static string Meters(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture) + " m";
        }
    }
}
