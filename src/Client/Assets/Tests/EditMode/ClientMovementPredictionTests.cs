using System;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class ClientMovementPredictionTests
    {
        private static readonly MovementSimulationSettings Settings = new MovementSimulationSettings(
            30,
            5f,
            720f,
            -24f,
            -2f,
            0f,
            -14f,
            14f,
            -14f,
            14f);

        [Test]
        public void ReconciliationRemovesAcknowledgedInputsAndReplaysPendingInputs()
        {
            var first = Input(1, Path(1, 0f, 3f));
            var second = Input(2, Path(2, 3f, -1f));
            var world = FixtureCollisionWorld.Create();
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
        public void RedundantBatchCarriesTheNewestPathUntilTheWorkerConfirmsIt()
        {
            var world = FixtureCollisionWorld.Create();
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
            var path = Path(5, 0f, 4f);
            for (uint sequence = 1; sequence <= 6; sequence++)
            {
                prediction.Predict(Input(sequence, path));
            }

            var batch = prediction.CreateRedundantInputBatch(out var unconfirmed);

            Assert.That(batch.Length, Is.EqualTo(4));
            Assert.That(batch[0].InputSequence, Is.EqualTo(3));
            Assert.That(batch[3].InputSequence, Is.EqualTo(6));
            Assert.That(unconfirmed, Is.SameAs(path));

            // An owner snapshot that already follows the path confirms it.
            prediction.Reconcile(prediction.State, 4);
            prediction.CreateRedundantInputBatch(out unconfirmed);
            Assert.That(prediction.ConfirmedPathId, Is.EqualTo(5u));
            Assert.That(unconfirmed, Is.Null);

            // Standing needs no path data at all.
            prediction.Predict(Input(7, null));
            prediction.CreateRedundantInputBatch(out unconfirmed);
            Assert.That(unconfirmed, Is.Null);
        }

        [Test]
        public void NavigatorPlansInTheBackgroundIssuesPathIdsAndStopsWithinRange()
        {
            var world = FixtureCollisionWorld.Create();
            var navigator = new LocalPlayerNavigator(
                world,
                Settings,
                MovementPathPlannerSettings.Default);
            var standing = PlayerMovementSimulation.CreateInitialState(Settings, world, 0f, 0f, -1f, 0f);
            var start = standing.Position;

            var walk = navigator.Request(start, new SimulationVector3(4f, 0f, 3f), 0f);
            WaitForPlan(navigator, standing, walk);
            Assert.That(navigator.LastResult, Is.EqualTo(NavigationResult.Moving));
            var first = navigator.CurrentPath;
            Assert.That(first.Id, Is.EqualTo(1u));
            Assert.That(first.Destination.X, Is.EqualTo(4f).Within(0.0001f));
            Assert.That(first.Destination.Z, Is.EqualTo(3f).Within(0.0001f));

            var approach = navigator.Request(start, new SimulationVector3(-4f, 0f, 3f), 2f);
            Assert.That(approach, Is.GreaterThan(walk));
            WaitForPlan(navigator, standing, approach);
            var approachPath = navigator.CurrentPath;
            Assert.That(approachPath.Id, Is.EqualTo(2u));
            var deltaX = approachPath.Destination.X + 4f;
            var deltaZ = approachPath.Destination.Z - 3f;
            Assert.That(Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ)), Is.EqualTo(2d).Within(0.001d));

            // A NaN destination has no route, so the current path stays.
            WaitForPlan(navigator, standing, navigator.Request(start, new SimulationVector3(float.NaN, 0f, 0f), 0f));
            Assert.That(navigator.LastResult, Is.EqualTo(NavigationResult.NoRoute));
            Assert.That(navigator.CurrentPath, Is.SameAs(approachPath));

            // An approach whose plan has no waypoints is no route either, not an arrival.
            WaitForPlan(navigator, standing, navigator.Request(start, new SimulationVector3(float.NaN, 0f, 0f), 2f));
            Assert.That(navigator.LastResult, Is.EqualTo(NavigationResult.NoRoute));
            Assert.That(navigator.CurrentPath, Is.SameAs(approachPath));

            WaitForPlan(navigator, standing, navigator.Request(start, new SimulationVector3(0.5f, 0f, -1f), 2f));
            Assert.That(navigator.LastResult, Is.EqualTo(NavigationResult.AlreadyThere));
            Assert.That(navigator.CurrentPath, Is.Null);

            // A newer request wins over one that is still being planned.
            navigator.Request(start, new SimulationVector3(4f, 0f, 3f), 0f);
            var newest = navigator.Request(start, new SimulationVector3(-4f, 0f, 3f), 0f);
            WaitForPlan(navigator, standing, newest);
            Assert.That(navigator.CurrentPath.Destination.X, Is.EqualTo(-4f).Within(0.0001f));
            var stop = navigator.Stop();
            Assert.That(navigator.AppliedIntentId, Is.EqualTo(stop));
            Assert.That(navigator.CurrentPath, Is.Null);
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
        public void PredictionAppliesCarryRevisionToMovementSpeed()
        {
            var world = FixtureCollisionWorld.Create();
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

            prediction.Predict(Input(1, Path(1, 0f, 5f)));

            Assert.That(prediction.CarryState, Is.SameAs(carryState));
            Assert.That(
                prediction.State.VelocityZ,
                Is.EqualTo(Settings.WalkSpeed * 0.9f).Within(0.0001f));
        }

        // The worker walks a character at the admin speed from its next tick; the client predicts
        // with the same settings from the next step.
        [Test]
        public void PredictionWalksAtTheAdminSpeedOnceItsSettingsChange()
        {
            var world = FixtureCollisionWorld.Create();
            var initial = PlayerMovementSimulation.CreateInitialState(Settings, world, 0f, 0f, -1f, 0f);
            var prediction = new ClientMovementPrediction(initial, Settings, PlayerCarryState.Default, world);

            prediction.Predict(Input(1, Path(1, 0f, 5f)));
            Assert.That(prediction.State.VelocityZ, Is.EqualTo(Settings.WalkSpeed).Within(0.0001f));
            prediction.ApplySettings(Settings.WithWalkSpeedPercent(300));
            prediction.Predict(Input(2, Path(1, 0f, 5f)));

            Assert.That(prediction.State.VelocityZ, Is.EqualTo(Settings.WalkSpeed * 3f).Within(0.0001f));
            Assert.Throws<System.ArgumentNullException>(() => prediction.ApplySettings(null));
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

            // The estimate kept running through the frame, so the clock lands about one delay
            // behind the newest tick.
            Assert.That(clock.CurrentTick, Is.EqualTo(126d).Within(0.5d));
            Assert.That(130d - clock.CurrentTick, Is.EqualTo(4d).Within(0.5d));
        }

        [Test]
        public void RemoteRenderClockFollowsALatencyIncreaseAtOnce()
        {
            var clock = new RemoteRenderClock();
            clock.Reset(100, 4);
            clock.Advance(1f / 60f, 30, 100, 4);

            // Half a second without snapshots, then tick 102 arrives: the latency grew by
            // about 13 ticks, far more than the render delay.
            for (var frame = 0; frame < 30; frame++)
            {
                clock.Advance(1f / 60f, 30, 100, 4);
            }

            clock.Advance(1f / 60f, 30, 102, 4);

            Assert.That(clock.EstimatedServerTick, Is.EqualTo(102d));
            Assert.That(clock.CurrentTick, Is.LessThanOrEqualTo(102d));
        }

        [Test]
        public void RemoteRenderClockPlaysJitteredSnapshotsAtAnEvenSpeed()
        {
            const int tickRateHz = 30;
            const int snapshotIntervalTicks = 2;
            const float frameSeconds = 1f / 60f;
            var clock = new RemoteRenderClock();
            var nextSnapshot = 0;
            long latestTick = -1;
            var previousTick = 0d;
            for (var frame = 1; frame <= 600; frame++)
            {
                var now = frame * frameSeconds;
                while (true)
                {
                    // Every snapshot takes 50 ms plus up to 10 ms of jitter either way.
                    var jitterSeconds = (((nextSnapshot * 7919) % 21) - 10) / 1000d;
                    var arrival = (nextSnapshot * snapshotIntervalTicks / (double)tickRateHz) + 0.05d + jitterSeconds;
                    if (arrival > now)
                    {
                        break;
                    }

                    latestTick = nextSnapshot * snapshotIntervalTicks;
                    nextSnapshot++;
                }

                if (latestTick < 0)
                {
                    continue;
                }

                var current = clock.Advance(frameSeconds, tickRateHz, latestTick, 4);
                if (frame > 120)
                {
                    // Measured against the newest received tick, the speed swung by about
                    // 30 percent either way with every snapshot.
                    var speed = (current - previousTick) / (frameSeconds * tickRateHz);
                    Assert.That(speed, Is.InRange(0.85d, 1.15d), "Frame " + frame);
                    Assert.That(latestTick - current, Is.GreaterThan(0d), "Frame " + frame);
                }

                previousTick = current;
            }
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
            var world = FixtureCollisionWorld.Create();
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
                LoadBakedCollisionRevision());

            var created = NetworkMovementSession.TryCreate(accepted, out _, out var error);

            Assert.That(created, Is.False);
            Assert.That(error, Does.Contain("simulation revision"));
        }

        [Test]
        public void NetworkSessionAppliesOnlyNewerCarryRevisions()
        {
            var accepted = CreateJoinAccepted(
                GameSimulationCompatibility.Revision,
                LoadBakedCollisionRevision());
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
                    720f,
                    -24f,
                    55f,
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
                    true),
                new RealtimeShardRules(false));
        }

        private static void WaitForPlan(
            LocalPlayerNavigator navigator,
            PlayerMovementState predicted,
            int intentId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((navigator.AppliedIntentId != intentId || navigator.IsPlanning)
                && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(1);
                navigator.Update(predicted);
            }

            Assert.That(navigator.AppliedIntentId, Is.EqualTo(intentId));
        }

        private static PlayerMovementInput Input(uint sequence, MovementPath path)
        {
            return new PlayerMovementInput(sequence, sequence, path);
        }

        private static MovementPath Path(uint id, float x, float z)
        {
            return new MovementPath(id, new[] { new SimulationVector3(x, 0f, z) });
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
                true);
        }

        // A network session streams the baked collision of the joined World, so join tests
        // must announce that revision.
        private static string LoadBakedCollisionRevision()
        {
            Assert.That(
                UnityWorldCollisionLoader.TryLoad(
                    "development-world-1",
                    out var collisionWorld,
                    out var error),
                Is.True,
                error);
            return collisionWorld.Revision;
        }
    }
}
