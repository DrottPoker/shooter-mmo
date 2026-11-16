using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;

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
            late.Push(100, State(0f, 0f), 2, 15, false);
            late.Push(130, State(0.3f, 0f), 2, 15, false);
            Assert.That(late.LastMovingIntervalTicks, Is.Zero);
            Assert.That(late.TrySample(127.9d, out var stood), Is.True);
            Assert.That(stood.PositionX, Is.Zero);
            Assert.That(late.TrySample(129d, out var walking), Is.True);
            Assert.That(walking.PositionX, Is.EqualTo(0.15f).Within(0.0001f));

            var longest = new RemoteMovementInterpolation();
            longest.Push(100, State(0f, 0f), 2, 15, false);
            longest.Push(115, State(1.5f, 0f), 2, 15, false);
            Assert.That(longest.LastMovingIntervalTicks, Is.EqualTo(15));
            Assert.That(longest.IsFirstMovingInterval, Is.True);
            Assert.That(longest.TrySample(101d, out var walked), Is.True);
            Assert.That(walked.PositionX, Is.EqualTo(0.1f).Within(0.0001f));

            var beyond = new RemoteMovementInterpolation();
            beyond.Push(100, State(0f, 0f), 2, 15, false);
            beyond.Push(116, State(0.3f, 0f), 2, 15, false);
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

                    if (!presenter.Advance(FrameSeconds, FrameSeconds))
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
