using NUnit.Framework;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.Diagnostics;
using ShooterMmo.Networking;
using ShooterMmo.Ui;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class ClientStabilityTests
    {
        [Test]
        public void SequentialOperationStateRejectsOverlappingOperations()
        {
            var state = new ClientOperationState();

            Assert.That(state.TryBegin(ClientOperation.JoinShard), Is.True);
            Assert.That(state.TryBegin(ClientOperation.RefreshShards), Is.False);
            Assert.That(state.Current, Is.EqualTo(ClientOperation.JoinShard));

            state.Complete(ClientOperation.RefreshShards);
            Assert.That(state.IsBusy, Is.True);

            state.Complete(ClientOperation.JoinShard);
            Assert.That(state.IsBusy, Is.False);
        }

        [Test]
        public void ProblemDetailsProducesStructuredApiError()
        {
            const string json =
                "{\"status\":401,\"detail\":\"Session expired.\",\"code\":\"invalid_session_token\",\"correlationId\":\"corr-1\"}";

            var error = ShooterMmoApiClient.ParseHttpError(401, json, "fallback");

            Assert.That(error.IsUnauthorized, Is.True);
            Assert.That(error.Code, Is.EqualTo("invalid_session_token"));
            Assert.That(error.Message, Is.EqualTo("Session expired."));
            Assert.That(error.CorrelationId, Is.EqualTo("corr-1"));
        }

        [Test]
        public void ReplacedAccountSessionPreservesTheDisconnectReason()
        {
            const string json =
                "{\"status\":401,\"detail\":\"This account logged in from another client.\",\"code\":\"account_session_replaced\"}";

            var error = ShooterMmoApiClient.ParseHttpError(401, json, "fallback");

            Assert.That(error.IsUnauthorized, Is.True);
            Assert.That(error.Code, Is.EqualTo(ClientSessionRecovery.AccountSessionReplacedCode));
            Assert.That(error.Message, Is.EqualTo("This account logged in from another client."));
        }

        [Test]
        public void SnapshotsOutsideJoinedStateAreIgnoredAsInFlightPackets()
        {
            Assert.That(
                RealtimeSimulationClient.ShouldIgnoreSimulationSnapshot(RealtimeConnectionState.Joining),
                Is.True);
            Assert.That(
                RealtimeSimulationClient.ShouldIgnoreSimulationSnapshot(RealtimeConnectionState.Leaving),
                Is.True);
            Assert.That(
                RealtimeSimulationClient.ShouldIgnoreSimulationSnapshot(RealtimeConnectionState.Disconnected),
                Is.True);
            Assert.That(
                RealtimeSimulationClient.ShouldIgnoreSimulationSnapshot(RealtimeConnectionState.Joined),
                Is.False);
        }

        [Test]
        public void ResourcesContainsClientEndpointConfiguration()
        {
            var config = ShooterMmoClientConfig.Load();

            Assert.That(config.AuthServiceBaseUrl, Is.EqualTo("http://localhost:5000"));
            Assert.That(config.RequestTimeoutSeconds, Is.EqualTo(10));
            Assert.That(config.RealtimeTimeoutSeconds, Is.EqualTo(10));
            Assert.That(config.SessionValidationIntervalSeconds, Is.EqualTo(5));
        }

        [Test]
        public void ClientLogFormatsCategoryAndNeutralizesLineBreaks()
        {
            var message = ClientLog.Format(
                ClientLogCategory.Simulation,
                "Connection rejected.\r\nTry again.");

            Assert.That(
                message,
                Is.EqualTo("[SIMULATION] Connection rejected.  Try again."));
        }

        [Test]
        public void WorldDebugFrameTelemetryCalculatesFpsAndPercentiles()
        {
            var tracker = new WorldDebugTelemetryTracker();

            for (var index = 0; index < 30; index++)
            {
                tracker.RecordFrame(1f / 60f);
            }

            Assert.That(tracker.FramesPerSecond, Is.EqualTo(60f).Within(0.1f));
            Assert.That(tracker.AverageFrameMilliseconds, Is.EqualTo(16.667f).Within(0.01f));
            Assert.That(tracker.P95FrameMilliseconds, Is.EqualTo(16.667f).Within(0.01f));
            Assert.That(tracker.MaximumFrameMilliseconds, Is.EqualTo(16.667f).Within(0.01f));
        }

        [Test]
        public void WorldDebugNetworkTelemetryCalculatesObservedRates()
        {
            var tracker = new WorldDebugTelemetryTracker();
            tracker.SampleNetwork(10f, 100, 1024, 50, 512, 80, 40);

            tracker.SampleNetwork(10.5f, 110, 3072, 55, 1536, 88, 47);

            Assert.That(tracker.ReceivedPacketsPerSecond, Is.EqualTo(20f));
            Assert.That(tracker.SentPacketsPerSecond, Is.EqualTo(10f));
            Assert.That(tracker.ReceivedKibibytesPerSecond, Is.EqualTo(4f));
            Assert.That(tracker.SentKibibytesPerSecond, Is.EqualTo(2f));
            Assert.That(tracker.SnapshotPacketsPerSecond, Is.EqualTo(16f));
            Assert.That(tracker.SnapshotFramesPerSecond, Is.EqualTo(14f));
        }

        [Test]
        public void WorldDebugNetworkCounterResetClearsRates()
        {
            var tracker = new WorldDebugTelemetryTracker();
            tracker.SampleNetwork(1f, 10, 1000, 10, 1000, 10, 10);
            tracker.SampleNetwork(1.5f, 20, 2000, 20, 2000, 20, 20);

            tracker.SampleNetwork(2f, 0, 0, 0, 0, 0, 0);

            Assert.That(tracker.ReceivedPacketsPerSecond, Is.Zero);
            Assert.That(tracker.SentPacketsPerSecond, Is.Zero);
            Assert.That(tracker.NetworkSampleSeconds, Is.Zero);
        }

        [Test]
        public void WorldDebugSnapshotLossUsesObservedAndMissingSequences()
        {
            Assert.That(
                WorldDebugDiagnostics.EstimateSnapshotLossPercent(95, 5),
                Is.EqualTo(5f));
            Assert.That(
                WorldDebugDiagnostics.EstimateSnapshotLossPercent(0, 0),
                Is.Zero);
        }
    }
}
