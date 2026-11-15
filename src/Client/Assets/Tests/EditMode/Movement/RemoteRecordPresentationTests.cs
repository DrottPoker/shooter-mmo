using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The remote record as the presentation reads it: the quantized position and facing, and the
    // velocity that the presenter derives from its two newest states, because the record carries
    // none.
    public sealed class RemoteRecordPresentationTests
    {
        private const int TickRateHz = 30;
        private const float FrameSeconds = 1f / 60f;

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
        public void AnEntityThatRanOutOfStatesWhileItMovedIsStarvedAndOneThatStoppedIsNot()
        {
            // The states carry no velocity, as records do, and the walk still starves.
            var walking = Presenter();
            walking.Push(100, State(0f, 0f));
            walking.Push(102, State(0.3f, 0f));
            Play(walking, 30);
            Assert.That(walking.IsPlayedOut, Is.True);
            Assert.That(walking.IsStarved, Is.True);

            var stopped = Presenter();
            stopped.Push(100, State(0f, 0f));
            stopped.Push(102, State(0.3f, 0f));
            stopped.Push(104, State(0.3f, 0f));
            Play(stopped, 30);
            Assert.That(stopped.IsPlayedOut, Is.True);
            Assert.That(stopped.IsStarved, Is.False);

            // A velocity in the state itself, as a spawn's initial state has, decides nothing.
            var standing = Presenter();
            standing.Push(100, new PlayerMovementState(1f, 0f, 1f, 4.5f, 0f, 0f, 0f, true));
            standing.Push(102, new PlayerMovementState(1f, 0f, 1f, 4.5f, 0f, 0f, 0f, true));
            Play(standing, 30);
            Assert.That(standing.IsPlayedOut, Is.True);
            Assert.That(standing.IsStarved, Is.False);
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

        private static void Play(RemoteMovementPresenter presenter, int frames)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                presenter.Advance(FrameSeconds, FrameSeconds);
            }
        }
    }
}
