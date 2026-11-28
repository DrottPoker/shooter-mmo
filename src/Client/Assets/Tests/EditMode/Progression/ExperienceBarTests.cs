using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // What the player sees of the level and experience: the bar's fraction and texts, the fill's
    // slide and its run over the top on a level-up, when gained experience and a level-up show,
    // the banner's fade, and the bar and banner over a progression state.
    public sealed class ExperienceBarTests
    {
        private const float Fill = ExperienceBarPresentation.FillSeconds;

        [Test]
        public void TheFractionIsTheShareOfTheLevelAndFullAtTheHighestLevel()
        {
            Assert.That(ExperienceBarPresentation.Fraction(340, 910), Is.EqualTo(340f / 910f).Within(1e-6f));
            Assert.That(ExperienceBarPresentation.Fraction(0, 910), Is.Zero);
            Assert.That(ExperienceBarPresentation.Fraction(5000, 910), Is.EqualTo(1f));
            Assert.That(ExperienceBarPresentation.Fraction(0, 0), Is.EqualTo(1f), "The highest level shows a full bar.");
            Assert.That(ExperienceBarPresentation.Fraction(null), Is.Zero);
        }

        [Test]
        public void TheTextsNameTheLevelAndTheExperienceWithThousandsSeparators()
        {
            var texts = FixtureTexts.Ui();
            Assert.That(ExperienceBarPresentation.LevelText(texts, 12), Is.EqualTo("Level 12"));
            Assert.That(ExperienceBarPresentation.LevelText(texts, 1200), Is.EqualTo("Level 1,200"));
            Assert.That(ExperienceBarPresentation.ExperienceText(texts, 340, 910), Is.EqualTo("340 / 910 XP"));
            Assert.That(ExperienceBarPresentation.ExperienceText(texts, 1234567, 13900000), Is.EqualTo("1,234,567 / 13,900,000 XP"));
            Assert.That(ExperienceBarPresentation.ExperienceText(texts, 0, 0), Is.EqualTo("Highest level"));
            Assert.That(ExperienceBarPresentation.GainText(texts, 25), Is.EqualTo("+25 XP"));
            Assert.That(ExperienceBarPresentation.GainText(texts, 12500), Is.EqualTo("+12,500 XP"));
        }

        // The snapshot after a join only states the progression, and a level that an admin tool
        // lowered shows no level-up.
        [Test]
        public void GainsAndLevelUpsShowForChangesButNotForASnapshotOrALoweredLevel()
        {
            var kill = Change(13, 0, 25, 1, RealtimeProgressionChangeReason.MobKill);
            var snapshot = Change(13, 340, 0, 1, RealtimeProgressionChangeReason.Snapshot);
            var quest = Change(12, 400, 400, 0, RealtimeProgressionChangeReason.QuestReward);
            var lowered = Change(5, 0, 0, -7, RealtimeProgressionChangeReason.Admin);
            var raised = Change(20, 0, 0, 8, RealtimeProgressionChangeReason.Admin);

            Assert.That(ExperienceBarPresentation.ShowsGain(kill), Is.True);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(kill), Is.True);
            Assert.That(ExperienceBarPresentation.ShowsGain(snapshot), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(snapshot), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsGain(quest), Is.True);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(quest), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsGain(lowered), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(lowered), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsGain(raised), Is.False);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(raised), Is.True);
            Assert.That(ExperienceBarPresentation.ShowsLevelUp(null), Is.False);
        }

        [Test]
        public void TheFillShowsTheFirstValueAtOnceAndSlidesToTheNext()
        {
            var fill = new ExperienceBarFill();
            Assert.That(fill.Fraction(0f), Is.Zero);

            fill.Set(0.2f, false, 10f);
            Assert.That(fill.Fraction(10f), Is.EqualTo(0.2f));
            Assert.That(fill.IsMoving(10f), Is.False);

            fill.Set(0.6f, false, 11f);
            var halfway = fill.Fraction(11f + (Fill / 2f));
            Assert.That(halfway, Is.GreaterThan(0.4f).And.LessThan(0.6f), "It eases out.");
            Assert.That(fill.IsMoving(11f + (Fill / 2f)), Is.True);
            Assert.That(fill.Fraction(11f + (Fill * 1.1f)), Is.EqualTo(0.6f));
            Assert.That(fill.IsMoving(11f + (Fill * 1.1f)), Is.False);
        }

        // A level-up runs to full first and then from empty to the new value; a gain that
        // arrives while it runs to full keeps the run and changes where it ends.
        [Test]
        public void ALevelUpRunsToFullAndStartsFromEmpty()
        {
            var fill = new ExperienceBarFill();
            fill.Set(0.7f, false, 0f);

            fill.Set(0.3f, true, 1f);
            Assert.That(fill.Fraction(1f + (Fill * 0.5f)), Is.GreaterThan(0.7f).And.LessThan(1f));
            Assert.That(fill.Fraction(1f + (Fill * 0.999f)), Is.GreaterThan(0.99f));
            Assert.That(fill.Fraction(1f + (Fill * 1.01f)), Is.LessThan(0.05f));
            Assert.That(fill.Fraction(1f + (Fill * 2.1f)), Is.EqualTo(0.3f));
            Assert.That(fill.IsMoving(1f + (Fill * 1.5f)), Is.True);
            Assert.That(fill.IsMoving(1f + (Fill * 2.1f)), Is.False);

            fill.Set(0.1f, true, 5f);
            fill.Set(0.4f, false, 5f + (Fill * 0.5f));
            Assert.That(fill.Target, Is.EqualTo(0.4f));
            Assert.That(fill.Fraction(5f + (Fill * 0.75f)), Is.GreaterThan(0.5f), "Still on its run to full.");
            Assert.That(fill.Fraction(5f + (Fill * 2.1f)), Is.EqualTo(0.4f));

            fill.Reset();
            Assert.That(fill.HasValue, Is.False);
            fill.Set(0.9f, true, 9f);
            Assert.That(fill.Fraction(9f), Is.EqualTo(0.9f), "After a reset the first value shows at once.");
        }

        [Test]
        public void TheBannerFadesInHoldsAndFadesOut()
        {
            Assert.That(ExperienceBarPresentation.BannerAlpha(0f), Is.Zero);
            Assert.That(ExperienceBarPresentation.BannerAlpha(0.1f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(ExperienceBarPresentation.BannerAlpha(0.21f), Is.EqualTo(1f));
            Assert.That(ExperienceBarPresentation.BannerAlpha(2.39f), Is.EqualTo(1f));
            Assert.That(ExperienceBarPresentation.BannerAlpha(2.7f), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(ExperienceBarPresentation.BannerAlpha(3.01f), Is.Zero);
            Assert.That(ExperienceBarPresentation.BannerSeconds, Is.EqualTo(3f).Within(1e-6f));
            Assert.That(ExperienceBarPresentation.GainOffset(0f), Is.Zero);
            Assert.That(ExperienceBarPresentation.GainOffset(ExperienceBarPresentation.GainSeconds), Is.EqualTo(28f));
            Assert.That(ExperienceBarPresentation.GainAlpha(0f), Is.EqualTo(1f));
            Assert.That(ExperienceBarPresentation.GainAlpha(ExperienceBarPresentation.GainSeconds), Is.Zero);
        }

        // The bar shows once the state has a progression, follows every change, keeps at most
        // four gains with the newest replacing the oldest, and hides when the state resets.
        [Test]
        public void TheBarFollowsTheProgressionState()
        {
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            // Active, so TextMesh Pro measures the texts the plate hugs.
            var host = new GameObject("ExperienceBar");
            try
            {
                var progression = new ProgressionClientState();
                var bar = host.AddComponent<ExperienceBar>();
                bar.Initialize(theme, progression, FixtureTexts.Ui());
                bar.Begin();
                Assert.That(bar.IsVisible, Is.False);

                progression.Apply(Change(12, 340, 0, 0, RealtimeProgressionChangeReason.Snapshot, 910));
                Assert.That(bar.IsVisible, Is.True);
                Assert.That((bar.LevelLabel, bar.NumbersLabel), Is.EqualTo(("Level 12", "340 / 910 XP")));
                Assert.That(bar.ShownFraction, Is.EqualTo(340f / 910f).Within(1e-5f));
                Assert.That(bar.GainTexts(), Is.Empty, "A snapshot shows no gain.");
                AssertPlateHugsTheRow(bar, theme);
                var narrowNumbers = bar.NumbersWidth;

                for (var gain = 1; gain <= 5; gain++)
                {
                    progression.Apply(Change(12, 340 + gain, gain, 0, RealtimeProgressionChangeReason.MobKill, 910));
                }

                Assert.That(bar.GainTexts(), Is.EqualTo(new[] { "+2 XP", "+3 XP", "+4 XP", "+5 XP" }));

                progression.Apply(Change(12, 1234567, 0, 0, RealtimeProgressionChangeReason.Admin, 13900000));
                Assert.That(bar.NumbersWidth, Is.GreaterThan(narrowNumbers), "Longer numbers widen their side.");
                AssertPlateHugsTheRow(bar, theme);

                progression.Apply(Change(200, 0, 0, 1, RealtimeProgressionChangeReason.QuestReward, 0));
                Assert.That(bar.NumbersLabel, Is.EqualTo("Highest level"));
                AssertPlateHugsTheRow(bar, theme);

                progression.Reset();
                Assert.That(bar.IsVisible, Is.False);
                Assert.That(bar.GainTexts(), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(theme);
            }
        }

        [Test]
        public void TheBannerShowsALevelUpButNotASnapshotOrALoweredLevel()
        {
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            var host = new GameObject("LevelUpBanner");
            host.SetActive(false);
            try
            {
                var progression = new ProgressionClientState();
                var banner = host.AddComponent<LevelUpBanner>();
                banner.Initialize(theme, progression, FixtureTexts.Ui());
                banner.Begin();

                progression.Apply(Change(12, 0, 0, 1, RealtimeProgressionChangeReason.Snapshot));
                progression.Apply(Change(5, 0, 0, -7, RealtimeProgressionChangeReason.Admin));
                Assert.That(banner.IsVisible, Is.False);

                progression.Apply(Change(13, 10, 900, 1, RealtimeProgressionChangeReason.MobKill));
                Assert.That(banner.IsVisible, Is.True);
                Assert.That((banner.CaptionLabel, banner.LevelLabel), Is.EqualTo(("Level up", "Level 13")));

                progression.Apply(Change(14, 10, 900, 1, RealtimeProgressionChangeReason.MobKill));
                Assert.That(banner.LevelLabel, Is.EqualTo("Level 14"), "A newer level-up starts it over.");

                progression.Reset();
                Assert.That(banner.IsVisible, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(theme);
            }
        }

        // The banner starts at an opacity of 0, so it must not hide for that on the frame it
        // starts: it fades in, holds, fades out, and hides only once its whole time passed.
        [Test]
        public void TheBannerStaysThroughItsFadeAndHidesAfterItsEnd()
        {
            var theme = ScriptableObject.CreateInstance<UiTheme>();
            var host = new GameObject("LevelUpBanner");
            host.SetActive(false);
            try
            {
                var progression = new ProgressionClientState();
                var banner = host.AddComponent<LevelUpBanner>();
                banner.Initialize(theme, progression, FixtureTexts.Ui());
                banner.Begin();

                progression.Apply(Change(3, 0, 100, 1, RealtimeProgressionChangeReason.MobKill));
                banner.Tick(Time.unscaledTime);
                Assert.That(banner.IsVisible, Is.True, "The frame of the level-up keeps it.");

                const float start = 100f;
                banner.Show(3, start);
                banner.Tick(start);
                Assert.That((banner.IsVisible, banner.Alpha), Is.EqualTo((true, 0f)));
                banner.Tick(start + 0.1f);
                Assert.That(banner.Alpha, Is.EqualTo(0.5f).Within(1e-3f));
                Assert.That(banner.IsVisible, Is.True);
                banner.Tick(start + 1.2f);
                Assert.That((banner.IsVisible, banner.Alpha), Is.EqualTo((true, 1f)));
                banner.Tick(start + 2.7f);
                Assert.That(banner.Alpha, Is.EqualTo(0.5f).Within(1e-3f));
                banner.Tick(start + ExperienceBarPresentation.BannerSeconds + 0.01f);
                Assert.That(banner.IsVisible, Is.False, "Gone after its end.");
                Assert.That(ExperienceBarPresentation.BannerFinished(ExperienceBarPresentation.BannerSeconds - 0.01f), Is.False);
                Assert.That(ExperienceBarPresentation.BannerFinished(ExperienceBarPresentation.BannerSeconds), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(theme);
            }
        }

        // The motes leave the ring through the first half second, all of them by its end.
        [Test]
        public void TheLevelUpMotesBurstOverTheFirstHalfSecond()
        {
            Assert.That(LevelUpEffects.MotesEmittedBy(0f), Is.Zero);
            Assert.That(LevelUpEffects.MotesEmittedBy(0.25f), Is.EqualTo(45));
            Assert.That(LevelUpEffects.MotesEmittedBy(0.5f), Is.EqualTo(LevelUpEffects.Motes));
            Assert.That(LevelUpEffects.MotesEmittedBy(2f), Is.EqualTo(LevelUpEffects.Motes));
            Assert.That(LevelUpEffects.Motes, Is.EqualTo(90));
            Assert.That(LevelUpEffects.EffectSeconds, Is.EqualTo(2.3f).Within(1e-5f));
        }

        // The world effect plays on the own character for a level-up but not for a snapshot or a
        // lowered level, and on another character the worker names when the client shows it.
        [Test]
        public void TheLevelUpEffectPlaysForALevelUpAndForAShownCharacter()
        {
            const ulong owner = 7;
            const ulong shown = 42;
            var harness = new RealtimeHubHarness();
            var host = new GameObject("LevelUpEffects");
            host.SetActive(false);
            var material = new Material(Shader.Find("Sprites/Default"));
            try
            {
                var progression = new ProgressionClientState();
                var effects = host.AddComponent<LevelUpEffects>();
                effects.Initialize(progression, harness.Hub.Progression, null, material);
                effects.UseCharacters(
                    (ulong entityId, out Vector3 position) =>
                    {
                        position = Vector3.zero;
                        return entityId == owner || entityId == shown;
                    },
                    () => owner);
                effects.Begin();

                progression.Apply(Change(12, 0, 0, 1, RealtimeProgressionChangeReason.Snapshot));
                progression.Apply(Change(5, 0, 0, -7, RealtimeProgressionChangeReason.Admin));
                Assert.That(effects.PlayingCount, Is.Zero);

                progression.Apply(Change(13, 10, 900, 1, RealtimeProgressionChangeReason.MobKill));
                Assert.That(effects.IsPlaying(owner), Is.True);

                harness.JoinSession();
                harness.Deliver(RealtimeProtocol.EncodeEntityLeveledUp(RealtimeHubMessages.LevelUp(shown, 30)));
                harness.Deliver(RealtimeProtocol.EncodeEntityLeveledUp(RealtimeHubMessages.LevelUp(99, 30)));
                Assert.That(effects.IsPlaying(shown), Is.True);
                Assert.That(effects.IsPlaying(99), Is.False, "An entity the client does not show is ignored.");
                Assert.That(effects.PlayingCount, Is.EqualTo(2));

                progression.Apply(Change(14, 10, 900, 1, RealtimeProgressionChangeReason.MobKill));
                Assert.That(effects.PlayingCount, Is.EqualTo(2), "A character plays one effect at a time.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(material);
                harness.Dispose();
            }
        }

        // The world effect plays only on a character the client shows.
        [Test]
        public void TheLevelUpEffectIgnoresACharacterTheClientDoesNotShow()
        {
            var host = new GameObject("LevelUpEffects");
            host.SetActive(false);
            var material = new Material(Shader.Find("Sprites/Default"));
            try
            {
                var effects = host.AddComponent<LevelUpEffects>();
                effects.Initialize(new ProgressionClientState(), null, new CombatantViewLocator(null, null, null), material);
                Assert.That(effects.Play(42), Is.False);
                Assert.That(effects.PlayingCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(material);
            }
        }

        // The plate lies behind the level, the bar, and the numbers with its padding, and the row
        // on it stays below the height the bar keeps for itself.
        private static void AssertPlateHugsTheRow(ExperienceBar bar, UiTheme theme)
        {
            var plate = bar.PlateRect;
            Assert.That(bar.LevelWidth, Is.GreaterThan(0f));
            Assert.That(bar.NumbersWidth, Is.GreaterThan(0f));
            Assert.That(plate.x, Is.EqualTo(-(bar.LevelWidth + 20f)).Within(0.01f), "Padding and gap left of the level.");
            Assert.That(
                plate.width,
                Is.EqualTo(bar.LevelWidth + bar.NumbersWidth + theme.ExperienceBarWidth + 40f).Within(0.01f));
            Assert.That(plate.height, Is.GreaterThanOrEqualTo(theme.ExperienceBarHeight + 12f));
            Assert.That(bar.PlateTop, Is.LessThanOrEqualTo(ExperienceBar.ReservedBottom));
        }

        private static RealtimeOwnerProgressionChanged Change(
            ushort level,
            long experience,
            long gained,
            int levelsGained,
            RealtimeProgressionChangeReason reason,
            long experienceToNextLevel = 1000)
        {
            return new RealtimeOwnerProgressionChanged(
                level,
                experience,
                experienceToNextLevel,
                level - 3,
                gained,
                levelsGained,
                reason);
        }
    }
}
