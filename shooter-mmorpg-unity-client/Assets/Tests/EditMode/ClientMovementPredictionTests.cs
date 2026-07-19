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
            var prediction = new ClientMovementPrediction(
                initial,
                Settings,
                PlayerCarryState.Default,
                world);
            prediction.Predict(first);
            prediction.Predict(second);
            var authoritativeAfterFirst = PlayerMovementSimulation.Step(
                initial,
                first,
                Settings,
                PlayerCarryState.Default,
                world);

            prediction.Reconcile(authoritativeAfterFirst, 1);

            var expected = PlayerMovementSimulation.Step(
                authoritativeAfterFirst,
                second,
                Settings,
                PlayerCarryState.Default,
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
            var prediction = new ClientMovementPrediction(
                initial,
                Settings,
                PlayerCarryState.Default,
                world);
            for (uint sequence = 1; sequence <= 6; sequence++)
            {
                prediction.Predict(Input(sequence, 0f, 1f));
            }

            var batch = prediction.CreateRedundantInputBatch();

            Assert.That(batch.Length, Is.EqualTo(4));
            Assert.That(batch[0].InputSequence, Is.EqualTo(3));
            Assert.That(batch[3].InputSequence, Is.EqualTo(6));
        }

        [TestCase(200, 10000)]
        [TestCase(210, 9000)]
        [TestCase(220, 8000)]
        [TestCase(240, 6000)]
        [TestCase(260, 4000)]
        [TestCase(280, 2000)]
        public void SharedEncumbranceUsesTheAuthoritativeFixedPointCurve(
            long carriedWeight,
            int expectedBasisPoints)
        {
            var carryState = new PlayerCarryState(1, carriedWeight, 200);

            Assert.That(
                carryState.MovementMultiplierBasisPoints,
                Is.EqualTo(expectedBasisPoints));
        }

        [Test]
        public void PredictionAppliesCarryRevisionToSprintAndMovementSpeed()
        {
            var world = LoadCollisionWorld();
            var initial = PlayerMovementSimulation.CreateInitialState(
                Settings,
                world,
                0f,
                0f,
                -1f,
                0f);
            var prediction = new ClientMovementPrediction(
                initial,
                Settings,
                PlayerCarryState.Default,
                world);
            var carryState = new PlayerCarryState(1, 210, 200);
            prediction.ApplyCarryState(carryState);

            prediction.Predict(new PlayerMovementInput(
                1,
                1,
                0f,
                1f,
                0f,
                PlayerMovementButtons.Sprint));

            Assert.That(prediction.CarryState, Is.SameAs(carryState));
            Assert.That(prediction.State.IsSprinting, Is.False);
            Assert.That(
                prediction.State.VelocityZ,
                Is.EqualTo(Settings.WalkSpeed * 0.9f).Within(0.0001f));
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
        public void RemoteRenderClockRestoresItsInterpolationBufferAfterANetworkStall()
        {
            var clock = new RemoteRenderClock();
            Assert.That(clock.Reset(100, 4), Is.EqualTo(96d));

            clock.Advance(1f, 30, 100, 4);
            Assert.That(clock.CurrentTick, Is.EqualTo(100d));

            clock.Advance(1f / 60f, 30, 130, 4);

            Assert.That(clock.CurrentTick, Is.EqualTo(126d));
            Assert.That(130d - clock.CurrentTick, Is.EqualTo(4d));
        }

        [Test]
        public void LocalPresentationInterpolatesPredictionAcrossRenderFrames()
        {
            var presentation = new LocalMovementPresentation();
            presentation.Reset(State(0f, 350f));
            presentation.Retarget(State(2f, 10f));

            presentation.Advance(1f / 60f, 1f / 30f);

            Assert.That(presentation.State.PositionX, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(presentation.State.YawDegrees, Is.EqualTo(0f).Within(0.0001f));

            presentation.Advance(1f / 60f, 1f / 30f);

            Assert.That(presentation.State.PositionX, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(presentation.State.YawDegrees, Is.EqualTo(10f).Within(0.0001f));
        }

        [Test]
        public void LocalPresentationShiftsInFlightStateDuringReconciliation()
        {
            var presentation = new LocalMovementPresentation();
            presentation.Reset(State(0f, 0f));
            presentation.Retarget(State(2f, 20f));
            presentation.Advance(1f / 60f, 1f / 30f);

            presentation.ApplySimulationCorrection(State(1.5f, 10f));

            Assert.That(presentation.State.PositionX, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(presentation.State.YawDegrees, Is.EqualTo(0f).Within(0.0001f));

            presentation.Advance(1f / 60f, 1f / 30f);

            Assert.That(presentation.State.PositionX, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(presentation.State.YawDegrees, Is.EqualTo(10f).Within(0.0001f));
        }

        [Test]
        public void GroundedPresentationRemovesSlopeSupportOffsetFromVisualHeight()
        {
            var world = LoadCollisionWorld();
            var state = PlayerMovementSimulation.CreateInitialState(
                Settings,
                world,
                0f,
                0.6f,
                -7f,
                0f);
            var queryBuffer = new CollisionQueryBuffer();

            var found = GroundedMovementPresentation.TryGetVisualHeight(
                state.PositionX,
                state.PositionY,
                state.PositionZ,
                state.IsGrounded,
                world,
                Settings.CharacterCollision,
                queryBuffer,
                out var visualHeight,
                out var groundNormal);

            Assert.That(found, Is.True);
            Assert.That(visualHeight, Is.LessThan(state.PositionY));
            Assert.That(GroundedMovementPresentation.IsFlatGround(groundNormal), Is.False);
        }

        [Test]
        public void GroundedVerticalPresentationSmoothsSmallStepHeightChanges()
        {
            var presentation = new GroundedVerticalPresentation();
            presentation.Reset(0f);

            var firstFrame = presentation.Update(
                0.3f,
                true,
                0.75f,
                GroundedMovementPresentation.StepSmoothingDurationSeconds,
                1f / 60f);

            Assert.That(firstFrame, Is.GreaterThan(0f));
            Assert.That(firstFrame, Is.LessThan(0.3f));

            for (var frame = 1; frame < 6; frame++)
            {
                presentation.Update(
                    0.3f,
                    true,
                    0.75f,
                    GroundedMovementPresentation.StepSmoothingDurationSeconds,
                    1f / 60f);
            }

            Assert.That(presentation.CurrentHeight, Is.EqualTo(0.3f).Within(0.0031f));
        }

        [Test]
        public void GroundedVerticalPresentationSnapsAirborneAndLargeChanges()
        {
            var presentation = new GroundedVerticalPresentation();
            presentation.Reset(0f);

            var airborne = presentation.Update(0.3f, false, 0.75f, 0.1f, 1f / 60f);
            var largeCorrection = presentation.Update(2f, true, 0.75f, 0.1f, 1f / 60f);

            Assert.That(airborne, Is.EqualTo(0.3f));
            Assert.That(largeCorrection, Is.EqualTo(2f));
        }

        [Test]
        public void NetworkSessionRejectsDifferentCollisionRevision()
        {
            var accepted = CreateJoinAccepted(
                GameSimulationCompatibility.Revision,
                "different-collision-revision");

            var created = NetworkMovementSession.TryCreate(accepted, out _, out var error);

            Assert.That(created, Is.False);
            Assert.That(error, Does.Contain("collision revision"));
        }

        [Test]
        public void NetworkSessionRejectsDifferentSimulationRevision()
        {
            var accepted = CreateJoinAccepted(
                "different-simulation-revision",
                LoadCollisionWorld().Revision);

            var created = NetworkMovementSession.TryCreate(accepted, out _, out var error);

            Assert.That(created, Is.False);
            Assert.That(error, Does.Contain("simulation revision"));
        }

        [Test]
        public void NetworkSessionAppliesOnlyNewerCarryRevisions()
        {
            var accepted = CreateJoinAccepted(
                GameSimulationCompatibility.Revision,
                LoadCollisionWorld().Revision);
            Assert.That(
                NetworkMovementSession.TryCreate(accepted, out var session, out var createError),
                Is.True,
                createError);

            Assert.That(
                session.TryApplyCarryState(
                    new RealtimeCarryState(1, 210, 200),
                    out var changed,
                    out var updateError),
                Is.True,
                updateError);
            Assert.That(changed, Is.True);
            Assert.That(session.CarryState.ItemStateRevision, Is.EqualTo(1));

            Assert.That(
                session.TryApplyCarryState(
                    new RealtimeCarryState(0, 0, 200),
                    out _,
                    out var staleError),
                Is.False);
            Assert.That(staleError, Does.Contain("older carry-state revision"));
        }

        private static RealtimeJoinAccepted CreateJoinAccepted(
            string simulationRevision,
            string collisionRevision)
        {
            return new RealtimeJoinAccepted(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                "Collision Hero",
                "local-shard-1",
                "development-world-1",
                1,
                simulationRevision,
                collisionRevision,
                DateTime.UtcNow.ToString("O"),
                DateTime.UtcNow.AddSeconds(30).ToString("O"),
                false,
                new RealtimeCarryState(0, 0, 200),
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
                    "development-world-1",
                    out var collisionWorld,
                    out var error),
                Is.True,
                error);
            return collisionWorld;
        }
    }
}
