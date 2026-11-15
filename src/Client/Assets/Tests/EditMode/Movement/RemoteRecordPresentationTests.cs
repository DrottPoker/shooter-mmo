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
