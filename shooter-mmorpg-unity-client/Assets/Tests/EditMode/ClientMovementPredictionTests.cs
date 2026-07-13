using System;
using NUnit.Framework;
using ShooterMmo.Collision;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using ShooterMmo.Networking;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class ClientMovementPredictionTests
    {
        private static readonly MovementSimulationSettings Settings = new MovementSimulationSettings(
            30,
            5f,
            8f,
            720f,
            -24f,
            7f,
            -2f,
            0f,
            -14f,
            14f,
            -14f,
            14f);

        [Test]
        public void ReconciliationRemovesAcknowledgedInputsAndReplaysPendingInputs()
        {
            var first = Input(1, 0f, 1f);
            var second = Input(2, 1f, 0f);
            var world = LoadCollisionWorld();
            var initial = PlayerMovementSimulation.CreateInitialState(
                Settings,
                world,
                0f,
                0f,
                -1f,
                0f);
            var prediction = new ClientMovementPrediction(initial, Settings, world);
            prediction.Predict(first);
            prediction.Predict(second);
            var authoritativeAfterFirst = PlayerMovementSimulation.Step(
                initial,
                first,
                Settings,
                world);

            prediction.Reconcile(authoritativeAfterFirst, 1);

            var expected = PlayerMovementSimulation.Step(
                authoritativeAfterFirst,
                second,
                Settings,
                world);
            Assert.That(prediction.PendingInputCount, Is.EqualTo(1));
            Assert.That(prediction.State.PositionX, Is.EqualTo(expected.PositionX).Within(0.0001f));
            Assert.That(prediction.State.PositionZ, Is.EqualTo(expected.PositionZ).Within(0.0001f));
        }

        [Test]
        public void RedundantBatchContainsAtMostFourNewestInputs()
        {
            var world = LoadCollisionWorld();
            var initial = PlayerMovementSimulation.CreateInitialState(
                Settings,
                world,
                0f,
                0f,
                -1f,
                0f);
            var prediction = new ClientMovementPrediction(initial, Settings, world);
            for (uint sequence = 1; sequence <= 6; sequence++)
            {
                prediction.Predict(Input(sequence, 0f, 1f));
            }

            var batch = prediction.CreateRedundantInputBatch();

            Assert.That(batch.Length, Is.EqualTo(4));
            Assert.That(batch[0].InputSequence, Is.EqualTo(3));
            Assert.That(batch[3].InputSequence, Is.EqualTo(6));
        }

        [Test]
        public void RemoteInterpolationSamplesBetweenServerTicksAndAcrossYawWrap()
        {
            var interpolation = new RemoteMovementInterpolation();
            Assert.That(interpolation.Push(10, State(0f, 350f)), Is.True);
            Assert.That(interpolation.Push(12, State(2f, 10f)), Is.True);

            var sampled = interpolation.TrySample(11d, out var state);

            Assert.That(sampled, Is.True);
            Assert.That(state.PositionX, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(state.YawDegrees, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(interpolation.Push(11, State(20f, 90f)), Is.False);
        }

        [Test]
        public void NetworkSessionRejectsDifferentCollisionRevision()
        {
            var accepted = new RealtimeJoinAccepted(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                "Collision Hero",
                "local-world-1",
                "different-collision-revision",
                DateTime.UtcNow.ToString("O"),
                DateTime.UtcNow.AddSeconds(30).ToString("O"),
                false,
                new RealtimeMovementSettings(
                    30,
                    15,
                    5f,
                    8f,
                    720f,
                    -24f,
                    55f,
                    7f,
                    -2f,
                    0f,
                    -14f,
                    14f,
                    -14f,
                    14f,
                    0.35f,
                    2f,
                    0.35f,
                    45f,
                    0.4f,
                    0.1f,
                    6),
                new RealtimePlayerState(
                    0f,
                    0f,
                    -1f,
                    0f,
                    -2f,
                    0f,
                    0f,
                    true,
                    false));

            var created = NetworkMovementSession.TryCreate(accepted, out _, out var error);

            Assert.That(created, Is.False);
            Assert.That(error, Does.Contain("collision revision"));
        }

        private static PlayerMovementInput Input(uint sequence, float moveX, float moveY)
        {
            return new PlayerMovementInput(
                sequence,
                sequence,
                moveX,
                moveY,
                0f,
                PlayerMovementButtons.None);
        }

        private static PlayerMovementState State(float positionX, float yawDegrees)
        {
            return new PlayerMovementState(
                positionX,
                0f,
                0f,
                0f,
                -2f,
                0f,
                yawDegrees,
                true,
                false);
        }

        private static ChunkedStaticCollisionWorld LoadCollisionWorld()
        {
            Assert.That(
                UnityWorldCollisionLoader.TryLoad(
                    "local-world-1",
                    out var collisionWorld,
                    out var error),
                Is.True,
                error);
            return collisionWorld;
        }
    }
}
