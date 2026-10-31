using Mmorpg.Client.Gameplay;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class HumanLocomotionTests
    {
        private const float WalkCycle = 28f / 30f;
        private const float JogCycle = 19f / 30f;

        [Test]
        public void EachClipPlaysAtItsOwnRateAtItsOwnSpeed()
        {
            AssertEvaluate(HumanLocomotion.WalkSpeed, HumanLocomotion.WalkSpeed, 1f);
            AssertEvaluate(HumanLocomotion.JogSpeed, HumanLocomotion.JogSpeed, 1f);
        }

        [Test]
        public void BelowTheWalkSpeedTheWalkPlaysSlowerDownToAMinimum()
        {
            // An encumbered character moves at a fifth of the game speed.
            AssertEvaluate(1f, HumanLocomotion.WalkSpeed, 1f / HumanLocomotion.WalkSpeed);
            AssertEvaluate(0.2f, HumanLocomotion.WalkSpeed, HumanLocomotion.MinimumRate);
            AssertEvaluate(-1f, HumanLocomotion.WalkSpeed, HumanLocomotion.MinimumRate);
        }

        [Test]
        public void BetweenWalkAndJogTheBlendedStrideMatchesTheSpeed()
        {
            const float speed = 3.2f;
            HumanLocomotion.Evaluate(speed, WalkCycle, JogCycle, out var blend, out var rate);
            Assert.That(blend, Is.EqualTo(speed));
            var weight = (speed - HumanLocomotion.WalkSpeed) / (HumanLocomotion.JogSpeed - HumanLocomotion.WalkSpeed);
            var cycle = (1f - weight) * WalkCycle + weight * JogCycle;
            var stride = (1f - weight) * HumanLocomotion.WalkSpeed * WalkCycle + weight * HumanLocomotion.JogSpeed * JogCycle;
            Assert.That(rate * stride / cycle, Is.EqualTo(speed).Within(0.0001f));
            Assert.That(rate, Is.GreaterThan(1f));
        }

        [Test]
        public void TheRateChangesSmoothlyWithTheSpeed()
        {
            var previous = 0f;
            for (var speed = 0.5f; speed <= 6f; speed += 0.01f)
            {
                HumanLocomotion.Evaluate(speed, WalkCycle, JogCycle, out _, out var rate);
                if (speed > 0.51f)
                {
                    Assert.That(rate - previous, Is.LessThan(0.02f).And.GreaterThan(-0.02f), speed.ToString());
                }

                previous = rate;
            }
        }

        [Test]
        public void AboveTheJogSpeedTheJogPlaysFaster()
        {
            AssertEvaluate(6f, HumanLocomotion.JogSpeed, 6f / HumanLocomotion.JogSpeed);
        }

        [Test]
        public void TheMeasuredSpeedIsSmoothedTowardTheMeasurement()
        {
            var speed = CharacterLocomotionAnimator.Smooth(0f, 5f, CharacterLocomotionAnimator.SmoothingSeconds);
            Assert.That(speed, Is.EqualTo(5f * (1f - UnityEngine.Mathf.Exp(-1f))).Within(0.0001f));
            Assert.That(CharacterLocomotionAnimator.Smooth(5f, 5f, 0.016f), Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void ABodyFallsOnlyWhenItsCharacterDiesInView()
        {
            Assert.That(HumanLocomotion.LifeChangeFor(true, false, true), Is.EqualTo(HumanLocomotion.LifeChange.Fall));
            Assert.That(HumanLocomotion.LifeChangeFor(true, true, true), Is.EqualTo(HumanLocomotion.LifeChange.None));
            Assert.That(HumanLocomotion.LifeChangeFor(true, false, false), Is.EqualTo(HumanLocomotion.LifeChange.None));
        }

        [Test]
        public void ABodyFirstSeenDeadLiesDownAtOnce()
        {
            Assert.That(HumanLocomotion.LifeChangeFor(false, false, true), Is.EqualTo(HumanLocomotion.LifeChange.LieDead));
            Assert.That(HumanLocomotion.LifeChangeFor(false, false, false), Is.EqualTo(HumanLocomotion.LifeChange.None));
        }

        [Test]
        public void ARespawnedBodyStandsAtOnce()
        {
            Assert.That(HumanLocomotion.LifeChangeFor(true, true, false), Is.EqualTo(HumanLocomotion.LifeChange.StandUp));
        }

        private static void AssertEvaluate(float speed, float expectedBlend, float expectedRate)
        {
            HumanLocomotion.Evaluate(speed, WalkCycle, JogCycle, out var blend, out var rate);
            Assert.That(blend, Is.EqualTo(expectedBlend).Within(0.0001f), "blend at " + speed);
            Assert.That(rate, Is.EqualTo(expectedRate).Within(0.0001f), "rate at " + speed);
        }
    }
}
