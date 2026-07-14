using NUnit.Framework;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.Diagnostics;
using ShooterMmo.Networking;

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
    }
}
