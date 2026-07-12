using NUnit.Framework;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.Gameplay;
using UnityEngine;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class ClientStabilityTests
    {
        [Test]
        public void SequentialOperationStateRejectsOverlappingOperations()
        {
            var state = new ClientOperationState();

            Assert.That(state.TryBegin(ClientOperation.JoinWorld), Is.True);
            Assert.That(state.TryBegin(ClientOperation.RefreshWorlds), Is.False);
            Assert.That(state.Current, Is.EqualTo(ClientOperation.JoinWorld));

            state.Complete(ClientOperation.RefreshWorlds);
            Assert.That(state.IsBusy, Is.True);

            state.Complete(ClientOperation.JoinWorld);
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
        public void ResourcesContainsClientEndpointConfiguration()
        {
            var config = ShooterMmoClientConfig.Load();

            Assert.That(config.AuthServiceBaseUrl, Is.EqualTo("http://localhost:5000"));
            Assert.That(config.WorldServerBaseUrl, Is.EqualTo("http://localhost:5100"));
            Assert.That(config.RequestTimeoutSeconds, Is.EqualTo(10));
        }

        [Test]
        public void InputActionControllersCanEnableAndDispose()
        {
            var player = new GameObject("InputActionTestPlayer");
            var cameraObject = new GameObject("InputActionTestCamera");

            try
            {
                player.AddComponent<CharacterController>();
                var playerController = player.AddComponent<LocalPlayerController>();
                var cameraController = cameraObject.AddComponent<ThirdPersonCameraController>();
                cameraController.SetTarget(playerController.CameraTarget);

                Assert.That(playerController.enabled, Is.True);
                Assert.That(cameraController.enabled, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(player);
            }
        }
    }
}
