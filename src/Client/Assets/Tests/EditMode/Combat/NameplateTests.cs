using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The rules of the Mob nameplates: which Mobs show one, the bar color and texts, and how the
    // health slides to a new value.
    public sealed class NameplateTests
    {
        private const float Slide = NameplatePresentation.HealthSlideSeconds;

        [Test]
        public void AggressiveMobsAreRedAndDefensiveMobsYellow()
        {
            Assert.That(
                NameplatePresentation.MobHealthColor(RealtimeWorldActorAggression.Aggressive),
                Is.EqualTo(NameplatePresentation.AggressiveColor));
            Assert.That(
                NameplatePresentation.MobHealthColor(RealtimeWorldActorAggression.Defensive),
                Is.EqualTo(NameplatePresentation.DefensiveColor));

            // A Mob without a known aggression shows as dangerous.
            Assert.That(
                NameplatePresentation.MobHealthColor(RealtimeWorldActorAggression.None),
                Is.EqualTo(NameplatePresentation.AggressiveColor));

            var red = NameplatePresentation.AggressiveColor;
            var yellow = NameplatePresentation.DefensiveColor;
            Assert.That(red.r, Is.GreaterThan(red.g * 3f));
            Assert.That(yellow.r, Is.GreaterThan(0.8f));
            Assert.That(yellow.g, Is.GreaterThan(0.7f));
            Assert.That(yellow.b, Is.LessThan(0.3f));
        }

        [Test]
        public void TextsShowCurrentAndMaximumHealthAndTheLevel()
        {
            Assert.That(NameplatePresentation.HealthText(56, 100), Is.EqualTo("56/100"));
            Assert.That(NameplatePresentation.HealthText(0, 50), Is.EqualTo("0/50"));
            Assert.That(NameplatePresentation.HealthText(1240, 10000), Is.EqualTo("1240/10000"));
            Assert.That(NameplatePresentation.LevelText(2), Is.EqualTo("2"));
            Assert.That(NameplatePresentation.LevelText(10000), Is.EqualTo("10000"));
            Assert.That(NameplatePresentation.LevelText(0), Is.Empty);
        }

        [Test]
        public void EveryLivingMobWithinFortyMetresShowsANameplate()
        {
            Assert.That(NameplatePresentation.MobRangeMetres, Is.EqualTo(40f));
            Assert.That(NameplatePresentation.ShowsMobNameplate(0f, RealtimeCombatLifeState.Alive), Is.True);
            Assert.That(NameplatePresentation.ShowsMobNameplate(40f * 40f, RealtimeCombatLifeState.Alive), Is.True);
            Assert.That(NameplatePresentation.ShowsMobNameplate(40.1f * 40.1f, RealtimeCombatLifeState.Alive), Is.False);
            Assert.That(NameplatePresentation.ShowsMobNameplate(100f, RealtimeCombatLifeState.Evading), Is.True);
            Assert.That(NameplatePresentation.ShowsMobNameplate(100f, RealtimeCombatLifeState.Dead), Is.False);
        }

        [Test]
        public void TheFirstValueShowsAtOnce()
        {
            var slide = new NameplateHealthSlide(Slide);
            slide.Set(50, 50, 10f);

            Assert.That(slide.IsSliding(10f), Is.False);
            Assert.That(slide.DisplayedHealth(10f), Is.EqualTo(50));
            Assert.That(slide.Fraction(10f), Is.EqualTo(1f));
            Assert.That(slide.Maximum, Is.EqualTo(50));
        }

        [Test]
        public void DamageSlidesTheBarAndNumbersDownInPointThreeSeconds()
        {
            Assert.That(Slide, Is.EqualTo(0.3f));
            var slide = new NameplateHealthSlide(Slide);
            slide.Set(100, 100, 10f);
            slide.Set(56, 100, 11f);

            Assert.That(slide.Target, Is.EqualTo(56));
            Assert.That(slide.DisplayedHealth(11f), Is.EqualTo(100));
            Assert.That(slide.IsSliding(11.1f), Is.True);

            // It eases out: most of the way is covered in the first half.
            var halfway = slide.Displayed(11f + (Slide * 0.5f));
            Assert.That(halfway, Is.LessThan(78f).And.GreaterThan(56f));

            var previous = 100;
            for (var step = 1; step <= 30; step++)
            {
                var shown = slide.DisplayedHealth(11f + (Slide * step / 30f));
                Assert.That(shown, Is.LessThanOrEqualTo(previous));
                Assert.That(shown, Is.GreaterThanOrEqualTo(56));
                previous = shown;
            }

            Assert.That(slide.IsSliding(11f + Slide + 0.001f), Is.False);
            Assert.That(slide.DisplayedHealth(11f + Slide), Is.EqualTo(56));
            Assert.That(slide.Fraction(11f + Slide), Is.EqualTo(0.56f).Within(0.0001f));
            Assert.That(slide.DisplayedHealth(20f), Is.EqualTo(56));
        }

        [Test]
        public void AHitDuringASlideSlidesOnFromWhereTheBarIs()
        {
            var slide = new NameplateHealthSlide(Slide);
            slide.Set(100, 100, 0f);
            slide.Set(60, 100, 1f);
            var reached = slide.Displayed(1.1f);
            slide.Set(20, 100, 1.1f);

            Assert.That(slide.Displayed(1.1f), Is.EqualTo(reached).Within(0.0001f));
            Assert.That(slide.IsSliding(1.1f + Slide - 0.01f), Is.True);
            Assert.That(slide.DisplayedHealth(1.1f + Slide), Is.EqualTo(20));
        }

        [Test]
        public void HealingSlidesUpAndAnUnchangedValueKeepsTheSlide()
        {
            var slide = new NameplateHealthSlide(Slide);
            slide.Set(10, 50, 0f);
            slide.Set(50, 50, 1f);

            Assert.That(slide.Displayed(1f + (Slide * 0.5f)), Is.GreaterThan(10f).And.LessThan(50f));
            slide.Set(50, 50, 1.2f);
            Assert.That(slide.IsSliding(1.2f), Is.True);
            Assert.That(slide.DisplayedHealth(1f + Slide), Is.EqualTo(50));
        }

        [Test]
        public void ANewMaximumAndAResetShowTheValueAtOnce()
        {
            var slide = new NameplateHealthSlide(Slide);
            slide.Set(50, 50, 0f);
            slide.Set(30, 50, 1f);
            slide.Set(240, 240, 1.05f);

            Assert.That(slide.IsSliding(1.05f), Is.False);
            Assert.That(slide.DisplayedHealth(1.05f), Is.EqualTo(240));

            slide.Set(100, 240, 2f);
            slide.Reset();
            slide.Set(100, 240, 2.05f);
            Assert.That(slide.IsSliding(2.05f), Is.False);
            Assert.That(slide.DisplayedHealth(2.05f), Is.EqualTo(100));
        }

        [Test]
        public void AZeroSlideShowsEveryValueAtOnce()
        {
            var slide = new NameplateHealthSlide(0f);
            slide.Set(50, 50, 0f);
            slide.Set(10, 50, 1f);

            Assert.That(slide.IsSliding(1f), Is.False);
            Assert.That(slide.DisplayedHealth(1f), Is.EqualTo(10));
            Assert.That(new NameplateHealthSlide(Slide).Fraction(0f), Is.Zero);
        }
    }
}
