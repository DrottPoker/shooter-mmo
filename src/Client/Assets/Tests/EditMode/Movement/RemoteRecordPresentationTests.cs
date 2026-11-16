using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;
using UnityEngine;
using Random = System.Random;

namespace Mmorpg.Client.Tests.EditMode
{
    // The remote record as the presentation reads it, and the rules for records that arrive at
    // uneven intervals: the velocity derived from the two newest states, a delay that follows the
    // interval, a repeated position that means the entity stands, the extrapolation that continues
    // and then eases to a stop, and the blend back when a newer state arrives.
    public sealed class RemoteRecordPresentationTests
    {
        private const int TickRateHz = 30;
        private const float FrameSeconds = 1f / 60f;

        // An entity walks 4.5 m/s: 0.15 m per tick, 0.075 m per frame at 60 frames per second.
        private const float MetresPerTick = 0.15f;
        private const float FrameStep = MetresPerTick / 2f;

        [Test]
        public void ARecordDecodesToItsPositionFacingAndGroundWithoutVelocity()
        {
            var record = RealtimeEntitySnapshot.Quantize(7, 12.3456f, -3.21f, -65.4321f, 271.3f, true);
            var packet = RealtimeProtocol.EncodeSimulationSnapshot(
                new RealtimeSimulationSnapshot(1, 2, 0, 1, new[] { record }));
            Assert.That(RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var snapshot, out var error), Is.True, error);

            var state = RemoteMovementPresenter.ToMovementState(snapshot.Entities[0]);

            // Within half a step of 1/128 m, 1/64 m, and 360/1024 degrees.
            Assert.That(state.PositionX, Is.EqualTo(12.3456f).Within(0.5f / 128f));
            Assert.That(state.PositionY, Is.EqualTo(-3.21f).Within(0.5f / 64f));
            Assert.That(state.PositionZ, Is.EqualTo(-65.4321f).Within(0.5f / 128f));
            Assert.That(state.YawDegrees, Is.EqualTo(271.3f).Within(360f / 1024f / 2f));
            Assert.That(state.IsGrounded, Is.True);
            Assert.That(state.VelocityX, Is.Zero);
            Assert.That(state.VelocityY, Is.Zero);
            Assert.That(state.VelocityZ, Is.Zero);

            var airborne = RemoteMovementPresenter.ToMovementState(
                RealtimeEntitySnapshot.Quantize(8, 0f, 1f, 0f, -0.1f, false));
            Assert.That(airborne.IsGrounded, Is.False);
            Assert.That(airborne.YawDegrees, Is.Zero, "A facing just below a full turn rounds to 0.");
        }

        [Test]
        public void TheVelocityComesFromTheTwoNewestStates()
        {
            var presenter = Presenter();
            Assert.That(presenter.TryGetDerivedVelocity(out _, out _), Is.False);

            presenter.Push(100, State(0f, 0f));
            Assert.That(presenter.TryGetDerivedVelocity(out _, out _), Is.False, "One state has no velocity.");

            // Two ticks at 30 ticks per second.
            presenter.Push(102, State(0.3f, -0.4f));
            Assert.That(presenter.TryGetDerivedVelocity(out var velocityX, out var velocityZ), Is.True);
            Assert.That(velocityX, Is.EqualTo(4.5f).Within(0.0001f));
            Assert.That(velocityZ, Is.EqualTo(-6f).Within(0.0001f));

            // A newer state at the same place, four ticks later, means it stands.
            presenter.Push(106, State(0.3f, -0.4f));
            Assert.That(presenter.TryGetDerivedVelocity(out velocityX, out velocityZ), Is.True);
            Assert.That(velocityX, Is.Zero);
            Assert.That(velocityZ, Is.Zero);
        }

        [Test]
        public void AWalkWithoutNewStatesContinuesThenEasesToAStopAndOnlyThenStarves()
        {
            var run = new Run(Presenter());
            run.Play(60, Walk);
            var lastState = run.LastPushedX;
            Assert.That(run.Starved.Any(starved => starved), Is.False);

            run.Play(60, _ => null);

            var extrapolated = lastState + (4.5f * (RemoteMovementPresenter.ExtrapolationSeconds + (RemoteMovementPresenter.ExtrapolationEaseSeconds / 2f)));
            Assert.That(run.X.Max(), Is.EqualTo(extrapolated).Within(0.02f), "Continued 0.25 s, then eased over 0.25 s.");
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep + 0.001f), "No jump.");
            Assert.That(run.MovedBackwards, Is.False);
            var firstStarved = run.Starved.IndexOf(true);
            Assert.That(firstStarved, Is.GreaterThan(0), "It starves once the extrapolation ran out.");
            Assert.That(run.X.Skip(firstStarved).Distinct().Count(), Is.EqualTo(1), "A starved entity stands.");
            Assert.That(run.X[firstStarved] - run.X[firstStarved - 1], Is.LessThan(0.001f), "It came to a stop before it starved.");
        }

        [Test]
        public void ANewerStateAfterTheExtrapolationBlendsBackWithoutAJump()
        {
            // The entity walks on through three lost snapshots.
            var walking = new Run(Presenter());
            walking.Play(60, Walk);
            walking.Play(60, tick => tick is >= 130 and <= 134 ? null : Walk(tick));
            Assert.That(walking.MaximumStep, Is.LessThanOrEqualTo(FrameStep + 0.01f));
            Assert.That(walking.MovedBackwards, Is.False);
            Assert.That(walking.X[walking.X.Count - 1] - walking.X[walking.X.Count - 2], Is.EqualTo(FrameStep).Within(0.005f));

            // The entity stopped where its last walking state was; the states after it were lost
            // and its stop repeats arrive late. The pose comes back at a bounded speed.
            var stopped = new Run(Presenter());
            stopped.Play(60, Walk);
            var stop = stopped.LastPushedX;
            stopped.Play(60, tick => tick <= 136 ? (float?)null : stop);
            Assert.That(stopped.X.Max(), Is.GreaterThan(stop + 0.2f), "It extrapolated past the stop.");
            Assert.That(stopped.MaximumStep, Is.LessThanOrEqualTo(FrameStep + (RemoteMovementPresenter.CorrectionMetresPerSecond * FrameSeconds) + 0.001f));
            Assert.That(stopped.X[stopped.X.Count - 1], Is.EqualTo(stop).Within(0.001f));
            Assert.That(stopped.Starved.Any(starved => starved), Is.False);
        }

        [Test]
        public void ACorrectionBeyondTheSnapDistanceSnaps()
        {
            var presenter = Presenter();
            var run = new Run(presenter);
            run.Play(60, Walk);
            run.Play(20, _ => null);
            var jumps = presenter.Discontinuities;

            // The next state is 10 m away from where the entity was shown.
            run.Play(4, tick => Walk(tick) + 10f);

            Assert.That(presenter.Discontinuities, Is.EqualTo(jumps + 1));
        }

        [Test]
        public void TheDelayFollowsTheIntervalOfTheMovingStatesWithoutAJumpOrAFreeze()
        {
            var presenter = Presenter();
            var run = new Run(presenter);
            run.Play(60, Walk);
            Assert.That(presenter.CurrentDelayTicks, Is.EqualTo(presenter.DelayTicks));

            // Far: a state every third snapshot, six ticks apart.
            run.Play(240, tick => (tick / 2) % 3 == 0 ? Walk(tick) : null);
            var farDelay = presenter.CurrentDelayTicks;
            Assert.That(farDelay, Is.GreaterThan(10d), "Two far intervals, at most half a second.");
            Assert.That(farDelay, Is.LessThanOrEqualTo(15d));

            // Near again.
            run.Play(240, Walk);
            Assert.That(presenter.CurrentDelayTicks, Is.LessThan(5d));

            Assert.That(run.MaximumDelayChange, Is.LessThanOrEqualTo((RemoteMovementPresenter.DelayChangePerTick * TickRateHz * FrameSeconds) + 0.0001d));
            Assert.That(run.MovedBackwards, Is.False);
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep * 1.5f), "No jump.");
            Assert.That(run.MinimumStepAfter(10), Is.GreaterThan(FrameStep / 4f), "No freeze.");
        }

        [Test]
        public void AStandingEntityIsNotStarvedAndAWalkAfterAStandDoesNotGlide()
        {
            var presenter = Presenter();
            var run = new Run(presenter);

            // Refreshes a second apart at the same place.
            run.Play(150, tick => tick % 30 == 0 ? 1f : (float?)null);
            Assert.That(run.Starved.Any(starved => starved), Is.False);
            Assert.That(run.X.Skip(3).Distinct().Single(), Is.EqualTo(1f));

            // It walks off at tick 190: the first walking state at 192 is 0.3 m along.
            run.Clear();
            run.Play(60, tick => tick < 192 ? (float?)null : 1f + (MetresPerTick * (tick - 190)));

            // The first step starts partway through a frame; every step after it is a full one.
            var moving = run.Steps.Where(step => step > 0.001f).ToArray();
            Assert.That(moving.Length, Is.GreaterThan(10));
            Assert.That(moving.Skip(1).Min(), Is.GreaterThan(FrameStep * 0.9f), "It walks, it does not glide.");
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep * 1.5f));
            Assert.That(run.Starved.Any(starved => starved), Is.False);
        }

        // A far walker comes into view: its first two states are 6 ticks and 1 m apart. The first
        // says nothing about standing, so the walker walked the whole gap; it is shown at its
        // walking speed, and the delay aims at two far intervals from the second state on.
        [Test]
        public void AFarWalkerThatComesIntoViewIsShownAtItsWalkingSpeed()
        {
            const float metresPerTick = 1f / 6f;
            var presenter = Presenter();
            var run = new Run(presenter);
            Func<uint, float?> farWalk = tick => (tick - 100) % 6 == 0 ? metresPerTick * (tick - 100) : (float?)null;

            // The second state arrives 6 ticks and 50 ms after the first, within the 16th frame.
            run.Play(16, farWalk);
            Assert.That(run.LastPushedX, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(presenter.TargetDelayTicks, Is.EqualTo(12d));

            // The following second.
            run.Play(60, farWalk);
            var walkingStep = metresPerTick * TickRateHz * FrameSeconds;
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(walkingStep * 1.2f), "No dash.");
            Assert.That(run.MovedBackwards, Is.False);
            Assert.That(run.X[run.X.Count - 1], Is.GreaterThan(2f), "It walks.");
            Assert.That(presenter.Discontinuities, Is.EqualTo(1));
        }

        // The worker refreshes interest on the tick before a snapshot, so a spawn leaves one tick
        // before the first record of its entity. That record takes the spawn's place as the first
        // state: the gap of one tick is not the entity's interval, so the far walker takes its
        // delay of two far intervals from its next record, as when the spawn came with the record.
        [Test]
        public void AFarWalkerWhoseSpawnLeftOneTickBeforeItsFirstRecordIsShownAtItsWalkingSpeed()
        {
            const float metresPerTick = 1f / 6f;
            var presenter = Presenter();
            var ticks = new uint[] { 99, 100, 106, 112, 118, 124, 130, 136, 142, 148 };
            var next = 0;
            float? last = null;
            var maximumStep = 0f;
            var movedBackwards = false;
            for (var frame = 1; frame <= 110; frame++)
            {
                var now = frame / 60d;
                while (next < ticks.Length && ((ticks[next] - 99) / (double)TickRateHz) + 0.05d <= now)
                {
                    presenter.Push(ticks[next], State(metresPerTick * ((int)ticks[next] - 100), 0f));
                    next++;
                    if (next == 2)
                    {
                        Assert.That(presenter.TargetDelayTicks, Is.EqualTo(presenter.DelayTicks), "One tick is no interval.");
                    }
                    else if (next == 3)
                    {
                        Assert.That(presenter.TargetDelayTicks, Is.EqualTo(12d));
                    }
                }

                if (next == 0)
                {
                    continue;
                }

                presenter.Advance(FrameSeconds, FrameSeconds);
                var x = presenter.Position.x;
                if (last is { } previous)
                {
                    maximumStep = Math.Max(maximumStep, Math.Abs(x - previous));
                    movedBackwards |= x - previous < -0.00001f;
                }

                last = x;
            }

            var walkingStep = metresPerTick * TickRateHz * FrameSeconds;
            Assert.That(next, Is.EqualTo(ticks.Length));
            Assert.That(maximumStep, Is.LessThanOrEqualTo(walkingStep * 1.2f), "No dash.");
            Assert.That(movedBackwards, Is.False);
            Assert.That(last, Is.GreaterThan(6f), "It walks.");
            Assert.That(presenter.Discontinuities, Is.EqualTo(1));
        }

        // A second state one tick after a first state of unknown standing takes its place, and one
        // a whole snapshot interval after it is the second state as before.
        [Test]
        public void ASecondStateWithinOneSnapshotIntervalOfTheFirstTakesItsPlace()
        {
            var refreshed = new RemoteMovementInterpolation();
            refreshed.Push(99, State(-0.1f, 0f), 2, 15, false, 2);
            Assert.That(refreshed.Push(100, State(0f, 0f), 2, 15, false, 2), Is.True);
            Assert.That(refreshed.Count, Is.EqualTo(1));
            Assert.That(refreshed.LastMovingIntervalTicks, Is.Zero);
            refreshed.Push(106, State(0.6f, 0f), 2, 15, false, 2);
            Assert.That(refreshed.LastMovingIntervalTicks, Is.EqualTo(6));
            Assert.That(refreshed.IsFirstMovingInterval, Is.True);
            Assert.That(refreshed.TrySample(103d, out var walking), Is.True);
            Assert.That(walking.PositionX, Is.EqualTo(0.3f).Within(0.0001f));

            var second = new RemoteMovementInterpolation();
            second.Push(100, State(0f, 0f), 2, 15, false, 2);
            second.Push(102, State(0.2f, 0f), 2, 15, false, 2);
            Assert.That(second.Count, Is.EqualTo(2));
            Assert.That(second.LastMovingIntervalTicks, Is.EqualTo(2));

            var standing = new RemoteMovementInterpolation();
            standing.Push(99, State(0f, 0f), 2, 15, true, 2);
            standing.Push(100, State(0.1f, 0f), 2, 15, true, 2);
            Assert.That(standing.Count, Is.EqualTo(2), "A first state that stands is kept.");
        }

        // A near walker comes into view: its first two states are 2 ticks apart, the interval a
        // new presenter expects, so it is shown as before: it stands at its first state for the
        // render delay and then walks a whole frame step in every frame.
        [Test]
        public void ANearWalkerThatComesIntoViewStartsAsBefore()
        {
            var presenter = Presenter();
            var run = new Run(presenter);
            run.Play(90, Walk);

            Assert.That(presenter.TargetDelayTicks, Is.EqualTo(presenter.DelayTicks));
            Assert.That(presenter.CurrentDelayTicks, Is.EqualTo(presenter.DelayTicks));
            Assert.That(presenter.Discontinuities, Is.EqualTo(1));
            var moving = run.Steps.SkipWhile(step => step < 0.001f).ToArray();
            Assert.That(run.Steps.Count - moving.Length, Is.LessThanOrEqualTo(8), "It stands at most the render delay.");
            Assert.That(moving.Skip(1).Min(), Is.GreaterThan(FrameStep * 0.9f));
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep * 1.1f));
            Assert.That(run.MovedBackwards, Is.False);
        }

        // An entity whose second state comes a second after its first, as the refresh of one
        // that stands does, stood: it starts to walk only one expected interval before that state.
        // A second state at most 15 ticks after the first, the longest render delay, walked the
        // whole gap.
        [Test]
        public void AnEntityWhoseSecondStateComesLateStoodUntilOneIntervalBeforeIt()
        {
            var late = new RemoteMovementInterpolation();
            late.Push(100, State(0f, 0f), 2, 15, false, 0);
            late.Push(130, State(0.3f, 0f), 2, 15, false, 0);
            Assert.That(late.LastMovingIntervalTicks, Is.Zero);
            Assert.That(late.TrySample(127.9d, out var stood), Is.True);
            Assert.That(stood.PositionX, Is.Zero);
            Assert.That(late.TrySample(129d, out var walking), Is.True);
            Assert.That(walking.PositionX, Is.EqualTo(0.15f).Within(0.0001f));

            var longest = new RemoteMovementInterpolation();
            longest.Push(100, State(0f, 0f), 2, 15, false, 0);
            longest.Push(115, State(1.5f, 0f), 2, 15, false, 0);
            Assert.That(longest.LastMovingIntervalTicks, Is.EqualTo(15));
            Assert.That(longest.IsFirstMovingInterval, Is.True);
            Assert.That(longest.TrySample(101d, out var walked), Is.True);
            Assert.That(walked.PositionX, Is.EqualTo(0.1f).Within(0.0001f));

            var beyond = new RemoteMovementInterpolation();
            beyond.Push(100, State(0f, 0f), 2, 15, false, 0);
            beyond.Push(116, State(0.3f, 0f), 2, 15, false, 0);
            Assert.That(beyond.LastMovingIntervalTicks, Is.Zero);
            Assert.That(beyond.TrySample(113.9d, out var stoodBeyond), Is.True);
            Assert.That(stoodBeyond.PositionX, Is.Zero);

            // Shown: the pose stands at the first state until the late state arrived, and then
            // walks from it at the walking speed.
            var presenter = Presenter();
            var run = new Run(presenter);
            run.Play(90, tick => tick == 100 ? 0f : tick >= 130 ? MetresPerTick * (tick - 128) : (float?)null);

            // The first pose is the third frame's and the late state arrives in the 63rd.
            Assert.That(run.X.Take(60).All(x => x == 0f), Is.True);
            Assert.That(run.X[run.X.Count - 1], Is.GreaterThan(1f));
            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep * 1.5f));
            Assert.That(run.MovedBackwards, Is.False);
        }

        // A far entity stands, refreshed once a second, and walks off at the start tick. The worker
        // sends its records of the two snapshots after the start, whatever its tier, and then one
        // on its far slot, the next of which comes one to three snapshots after the first record.
        // The walk starts from the stand one snapshot interval before the first record, and the
        // second gives the walking speed before the render time passes the first, so the entity is
        // never shown faster than it walks, also when it walked off between two snapshots.
        [TestCase(201u, 1u)]
        [TestCase(201u, 2u)]
        [TestCase(201u, 3u)]
        [TestCase(202u, 1u)]
        [TestCase(202u, 2u)]
        [TestCase(202u, 3u)]
        public void AFarEntityThatWalksOffFromAStandIsShownAtItsOwnSpeed(uint startTick, uint snapshotsToFarSlot)
        {
            var firstRecord = startTick + 2 - (startTick % 2);
            var farSlot = firstRecord + (2 * snapshotsToFarSlot);
            Func<uint, float?> far = tick =>
            {
                if (tick <= startTick)
                {
                    return tick % 30 == 0 ? 1f : (float?)null;
                }

                if (tick != firstRecord && tick != firstRecord + 2 && (tick < farSlot || (tick - farSlot) % 6 != 0))
                {
                    return null;
                }

                return 1f + (MetresPerTick * (tick - startTick));
            };

            var presenter = Presenter();
            var run = new Run(presenter);
            run.Play(600, far);

            Assert.That(run.MaximumStep, Is.LessThanOrEqualTo(FrameStep * 1.2f), "Never faster than it walks.");
            Assert.That(run.MovedBackwards, Is.False);
            Assert.That(run.Starved.Any(starved => starved), Is.False);
            Assert.That(presenter.Discontinuities, Is.EqualTo(1));
            Assert.That(run.X[run.X.Count - 1], Is.GreaterThan(20f), "It walks.");
        }

        // A first state that the caller knows the entity stands at, such as a Mob's rest pose,
        // keeps the start from a stand.
        [Test]
        public void AFirstStateKnownToStandStartsTheWalkFromAStand()
        {
            var presenter = Presenter();
            presenter.PushStanding(100, State(0f, 0f));
            presenter.Push(106, State(0.3f, 0f));
            Assert.That(presenter.TargetDelayTicks, Is.EqualTo(presenter.DelayTicks), "No interval was observed.");

            var unknown = Presenter();
            unknown.Push(100, State(0f, 0f));
            unknown.Push(106, State(1f, 0f));
            Assert.That(unknown.TargetDelayTicks, Is.EqualTo(12d), "The first interval counts at once.");
        }

        // A remote player that stands rests: from the frame its pose reached its standing newest
        // state until its next state it presents nothing and asks the collision world nothing.
        // It is shown exactly as one advanced every frame, which the reference is, given a new
        // ground revision in every frame so it never rests: through stands with refreshes, walks
        // off, lost states with extrapolation and starvation, a far walk, and stops, at frame
        // times between 30 and 144 frames per second.
        [Test]
        public void ARestingEntityIsShownExactlyAsOneAdvancedEveryFrame()
        {
            var resting = Presenter();
            var everyFrame = Presenter();
            var random = new Random(7);
            var time = 0d;
            var nextTick = 100u;
            var rested = 0;
            var starved = 0;
            for (var frame = 1; frame <= 2400; frame++)
            {
                var delta = 1f / random.Next(30, 145);
                time += delta;
                while (((nextTick - 100) / (double)TickRateHz) + 0.05d <= time)
                {
                    if (Wander(nextTick) is { } x)
                    {
                        var yaw = 90f + (nextTick % 7);
                        resting.Push(nextTick, new PlayerMovementState(x, 0f, 0f, 0f, 0f, 0f, yaw, true));
                        everyFrame.Push(nextTick, new PlayerMovementState(x, 0f, 0f, 0f, 0f, 0f, yaw, true));
                    }

                    nextTick += 2;
                }

                if (!resting.Advance(delta, delta, 0) && resting.HasPose)
                {
                    rested++;
                }

                everyFrame.Advance(delta, delta, frame);
                Assert.That(resting.Position.x, Is.EqualTo(everyFrame.Position.x).Within(0.0001f), "frame " + frame);
                Assert.That(resting.Position.y, Is.EqualTo(everyFrame.Position.y), "frame " + frame);
                Assert.That(resting.YawDegrees, Is.EqualTo(everyFrame.YawDegrees).Within(0.001f), "frame " + frame);
                Assert.That(resting.IsStarved, Is.EqualTo(everyFrame.IsStarved), "frame " + frame);
                Assert.That(resting.Discontinuities, Is.EqualTo(everyFrame.Discontinuities), "frame " + frame);
                starved += everyFrame.IsStarved ? 1 : 0;
            }

            Assert.That(nextTick, Is.GreaterThan(WanderEndTick), "the whole scenario played");
            Assert.That(starved, Is.GreaterThan(0), "the lost states starved it for a while");
            Assert.That(rested, Is.GreaterThan(800), "it rested through most of its stands, " + rested + " of 2400 frames");
        }

        // A resting player lands on ground that appears under it, such as a collision chunk that
        // loaded as the local player came near: the new ground revision ends the rest. Until then
        // it shows its replicated height and asks nothing.
        [Test]
        public void ARestingEntityLandsOnGroundThatLoadsUnderIt()
        {
            var settings = new CharacterCollisionSettings(0.4f, 1.8f, 0.35f, 45f, 0.2f, 0.25f, 4);
            var ground = new LoadingWorld(SlopeWorld(30f));
            var presenter = new RemoteMovementPresenter();
            presenter.Configure(TickRateHz, RemoteMovementPresenter.InterpolationDelayTicks(TickRateHz, 15), ground, settings);

            // The worker's capsule stands on the slope with its feet above the surface by its
            // support height.
            var standingY = 0.4f * ((1f / Mathf.Cos(30f * Mathf.Deg2Rad)) - 1f);
            ground.Loaded = true;
            Assert.That(
                GroundedMovementPresentation.TryGetVisualHeight(0f, standingY, 0f, true, ground, settings, new CollisionQueryBuffer(), out var landed, out _),
                Is.True);
            Assert.That(landed, Is.LessThan(standingY - 0.03f));
            ground.Loaded = false;

            var standing = new PlayerMovementState(0f, standingY, 0f, 0f, 0f, 0f, 90f, true);
            presenter.Push(100, standing);
            presenter.Push(102, standing);
            for (var frame = 0; frame < 120 && !presenter.IsResting; frame++)
            {
                presenter.Advance(FrameSeconds, FrameSeconds, 0);
            }

            Assert.That(presenter.IsResting, Is.True);
            Assert.That(presenter.Position.y, Is.EqualTo(standingY));

            ground.Loaded = true;
            var asked = ground.Queries;
            for (var frame = 0; frame < 30; frame++)
            {
                Assert.That(presenter.Advance(FrameSeconds, FrameSeconds, 0), Is.False);
            }

            Assert.That(ground.Queries, Is.EqualTo(asked), "a resting pose asks the collision world nothing");
            Assert.That(presenter.Position.y, Is.EqualTo(standingY));

            Assert.That(presenter.Advance(FrameSeconds, FrameSeconds, 1), Is.True);
            Assert.That(presenter.Position.y, Is.EqualTo(landed).Within(0.00001f));
            Assert.That(presenter.IsResting, Is.True, "it rests again on the new ground");
        }

        // Where a wandering entity is at a tick, or null when its record is not sent: it stands
        // with a refresh once a second, walks off, loses states and walks on, stops, walks far
        // with a state every third snapshot, and stands again until WanderEndTick.
        private static float? Wander(uint tick)
        {
            const uint walkFrom = 160;
            const uint stopAt = 300;
            const uint farFrom = 420;
            const uint farTo = 520;
            if (tick < walkFrom)
            {
                return tick == 100 || tick % 30 == 0 ? 0f : (float?)null;
            }

            if (tick < stopAt)
            {
                return tick is >= 240 and < 270 ? null : MetresPerTick * (tick - walkFrom);
            }

            var stop = MetresPerTick * (stopAt - walkFrom);
            if (tick < farFrom)
            {
                return tick < stopAt + 6 || tick % 30 == 0 ? stop : (float?)null;
            }

            if (tick < farTo)
            {
                return tick % 6 == 0 ? stop + (MetresPerTick * (tick - farFrom)) : (float?)null;
            }

            var far = stop + (MetresPerTick * (farTo - farFrom));
            return tick < farTo + 6 || tick % 30 == 0 ? far : (float?)null;
        }

        private const uint WanderEndTick = 640;

        // A slope of the given angle down toward negative z, whose surface passes through the
        // origin, ten meters wide and long.
        private static ICollisionWorld SlopeWorld(float degrees)
        {
            var radians = degrees * Mathf.Deg2Rad;
            const float halfHeight = 0.25f;
            var bake = CollisionWorldCompiler.Compile(new CollisionWorldAuthoringDocument
            {
                FormatVersion = CollisionDataFormat.Version,
                WorldId = "rest-tests",
                ChunkSize = 32f,
                Boxes = new[]
                {
                    new CollisionBoxAuthoringEntry
                    {
                        SourcePath = "Test/Slope#BoxCollider0",
                        LayerMask = CollisionLayers.CharacterMovement,
                        CenterX = 0f,
                        CenterY = -halfHeight * Mathf.Cos(radians),
                        CenterZ = -halfHeight * Mathf.Sin(radians),
                        HalfExtentX = 5f,
                        HalfExtentY = halfHeight,
                        HalfExtentZ = 5f,
                        RotationX = Mathf.Sin(radians * 0.5f),
                        RotationW = Mathf.Cos(radians * 0.5f)
                    }
                },
                Meshes = new CollisionMeshAuthoringEntry[0],
                Heightfields = new CollisionHeightfieldAuthoringEntry[0],
                Water = new CollisionWaterAuthoringEntry[0]
            });
            return new ChunkedStaticCollisionWorld(
                bake.Manifest.WorldId,
                bake.Manifest.Revision,
                bake.Manifest.ChunkSize,
                bake.Chunks.Select(chunk => CollisionChunkCodec.Decode(chunk.Data)));
        }

        // Ground that is not there until it loads, counting how often it is asked for shapes.
        private sealed class LoadingWorld : ICollisionWorld
        {
            private readonly ICollisionWorld world;

            public LoadingWorld(ICollisionWorld world)
            {
                this.world = world;
            }

            public bool Loaded { get; set; }

            public int Queries { get; private set; }

            public void QueryShapes(CollisionAabb bounds, uint layerMask, CollisionQueryBuffer buffer)
            {
                Queries++;
                if (Loaded)
                {
                    world.QueryShapes(bounds, layerMask, buffer);
                }
            }
        }

        private static RemoteMovementPresenter Presenter()
        {
            var presenter = new RemoteMovementPresenter();
            presenter.Configure(
                TickRateHz,
                RemoteMovementPresenter.InterpolationDelayTicks(TickRateHz, 15),
                null,
                new CharacterCollisionSettings(0.4f, 1.8f, 0.35f, 45f, 0.2f, 0.25f, 4));
            return presenter;
        }

        private static PlayerMovementState State(float positionX, float positionZ)
        {
            return new PlayerMovementState(positionX, 0f, positionZ, 0f, 0f, 0f, 90f, true);
        }

        // Where the walking entity is at a tick; it started at tick 100.
        private static float? Walk(uint tick)
        {
            return MetresPerTick * (tick - 100);
        }

        // Plays frames at 60 per second. The server sends a snapshot every two ticks from tick
        // 100, each arriving 50 ms after its tick; state tells whether the entity's record is in
        // it and where it is.
        private sealed class Run
        {
            private readonly RemoteMovementPresenter presenter;
            private uint nextTick = 100;
            private int frame;
            private double? lastDelay;

            public Run(RemoteMovementPresenter presenter)
            {
                this.presenter = presenter;
            }

            public List<float> X { get; } = new List<float>();

            public List<float> Steps { get; } = new List<float>();

            public List<bool> Starved { get; } = new List<bool>();

            public float LastPushedX { get; private set; }

            public float MaximumStep { get; private set; }

            public double MaximumDelayChange { get; private set; }

            public bool MovedBackwards { get; private set; }

            public void Clear()
            {
                X.Clear();
                Steps.Clear();
                Starved.Clear();
                MaximumStep = 0f;
                MovedBackwards = false;
            }

            public float MinimumStepAfter(int frames)
            {
                return Steps.Skip(frames).Min();
            }

            public void Play(int frames, Func<uint, float?> state)
            {
                for (var index = 0; index < frames; index++)
                {
                    frame++;
                    var now = frame / 60d;
                    while (((nextTick - 100) / (double)TickRateHz) + 0.05d <= now)
                    {
                        if (state(nextTick) is { } x)
                        {
                            presenter.Push(nextTick, State(x, 0f));
                            LastPushedX = x;
                        }

                        nextTick += 2;
                    }

                    // A resting pose presents nothing new and is shown where it was.
                    presenter.Advance(FrameSeconds, FrameSeconds);
                    if (!presenter.HasPose)
                    {
                        continue;
                    }

                    var position = presenter.Position.x;
                    if (X.Count > 0)
                    {
                        var step = position - X[X.Count - 1];
                        Steps.Add(step);
                        MaximumStep = Math.Max(MaximumStep, Math.Abs(step));
                        MovedBackwards |= step < -0.00001f;
                    }

                    if (lastDelay is { } previousDelay)
                    {
                        MaximumDelayChange = Math.Max(MaximumDelayChange, Math.Abs(presenter.CurrentDelayTicks - previousDelay));
                    }

                    lastDelay = presenter.CurrentDelayTicks;
                    X.Add(position);
                    Starved.Add(presenter.IsStarved);
                }
            }
        }
    }
}
