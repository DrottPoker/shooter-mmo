using System.Globalization;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The rules of what the player sees of the character's level and experience: the bar's
    // fraction and texts, when gained experience and a level-up show, and how long the banner
    // stays. Everything here reads the worker's progression and computes no level or experience.
    public static class ExperienceBarPresentation
    {
        // How long the fill takes to reach a new value, and on a level-up each of its two runs.
        public const float FillSeconds = 0.4f;

        // A gained experience label rises this far while it fades out, at most this many at once.
        public const float GainSeconds = 1.4f;
        public const float GainRise = 28f;
        public const int MaximumGains = 4;

        // The level-up banner fades in, holds, and fades out.
        public const float BannerFadeInSeconds = 0.2f;
        public const float BannerHoldSeconds = 2.2f;
        public const float BannerFadeOutSeconds = 0.6f;
        public const float BannerSeconds = BannerFadeInSeconds + BannerHoldSeconds + BannerFadeOutSeconds;

        // The share of the level's experience gathered, full at the highest level.
        public static float Fraction(long experience, long experienceToNextLevel)
        {
            if (experienceToNextLevel <= 0)
            {
                return 1f;
            }

            return Mathf.Clamp01((float)((double)experience / experienceToNextLevel));
        }

        public static float Fraction(RealtimeOwnerProgressionChanged progression)
        {
            return progression != null ? Fraction(progression.Experience, progression.ExperienceToNextLevel) : 0f;
        }

        // Such as "Level 12".
        public static string LevelText(TextCatalog texts, int level)
        {
            return texts.Format("ui/common/level", MessageArgument.Text("level", Number(level)));
        }

        // Such as "340 / 910 XP", or "Highest level" where experience stops.
        public static string ExperienceText(TextCatalog texts, long experience, long experienceToNextLevel)
        {
            if (experienceToNextLevel <= 0)
            {
                return texts.Get("ui/progression/highest-level");
            }

            return texts.Format(
                "ui/progression/experience",
                MessageArgument.Text("experience", Number(experience)),
                MessageArgument.Text("needed", Number(experienceToNextLevel)));
        }

        // Such as "+25 XP".
        public static string GainText(TextCatalog texts, long experienceGained)
        {
            return texts.Format("ui/progression/experience-gained", MessageArgument.Text("xp", Number(experienceGained)));
        }

        // A number with thousands separators, as the gold amounts show.
        public static string Number(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        // A change that added experience shows it; the snapshot after a join only states it.
        public static bool ShowsGain(RealtimeOwnerProgressionChanged change)
        {
            return change != null
                && change.Reason != RealtimeProgressionChangeReason.Snapshot
                && change.ExperienceGained > 0;
        }

        // A change that raised the level shows the banner and the world effect; a snapshot and a
        // level that an admin tool lowered show neither.
        public static bool ShowsLevelUp(RealtimeOwnerProgressionChanged change)
        {
            return change != null
                && change.Reason != RealtimeProgressionChangeReason.Snapshot
                && change.LevelsGained > 0;
        }

        // The banner is over once its whole time passed; its opacity starts at 0 too, so the
        // opacity never says whether it is over.
        public static bool BannerFinished(float age)
        {
            return age >= BannerSeconds;
        }

        // The banner's opacity at an age, from 0 through 1 and back to 0 once it is over.
        public static float BannerAlpha(float age)
        {
            if (age < 0f || age >= BannerSeconds)
            {
                return 0f;
            }

            if (age < BannerFadeInSeconds)
            {
                return age / BannerFadeInSeconds;
            }

            var fadeOutStart = BannerFadeInSeconds + BannerHoldSeconds;
            return age <= fadeOutStart ? 1f : 1f - ((age - fadeOutStart) / BannerFadeOutSeconds);
        }

        // A gained experience label's rise and opacity at an age.
        public static float GainOffset(float age)
        {
            return GainRise * Mathf.Clamp01(age / GainSeconds);
        }

        public static float GainAlpha(float age)
        {
            var progress = Mathf.Clamp01(age / GainSeconds);
            return 1f - (progress * progress);
        }
    }

    // The fill the experience bar shows: it moves to each new value over FillSeconds, easing out,
    // from where it is. On a level-up it first runs to full and then from empty to the new value;
    // a gain that arrives while it runs to full keeps the run and changes where it ends. The first
    // value shows at once.
    public sealed class ExperienceBarFill
    {
        private float from;
        private float to;
        private float startedAt;
        private bool hasValue;
        private bool wrapping;
        private float wrapTarget;

        public bool HasValue
        {
            get { return hasValue; }
        }

        // Where the fill ends once it stops moving.
        public float Target
        {
            get { return wrapping ? wrapTarget : to; }
        }

        public void Reset()
        {
            hasValue = false;
            wrapping = false;
        }

        public void Set(float target, bool levelUp, float now)
        {
            target = Mathf.Clamp01(target);
            if (!hasValue)
            {
                hasValue = true;
                wrapping = false;
                from = target;
                to = target;
                startedAt = float.NegativeInfinity;
                return;
            }

            if (!levelUp && wrapping && now - startedAt < ExperienceBarPresentation.FillSeconds)
            {
                wrapTarget = target;
                return;
            }

            var current = Fraction(now);
            startedAt = now;
            from = current;
            wrapping = levelUp;
            if (levelUp)
            {
                to = 1f;
                wrapTarget = target;
            }
            else
            {
                to = target;
            }
        }

        public bool IsMoving(float now)
        {
            if (!hasValue)
            {
                return false;
            }

            var runs = wrapping ? 2f : 1f;
            return now - startedAt < runs * ExperienceBarPresentation.FillSeconds;
        }

        public float Fraction(float now)
        {
            if (!hasValue)
            {
                return 0f;
            }

            var progress = (now - startedAt) / ExperienceBarPresentation.FillSeconds;
            if (wrapping && progress >= 1f)
            {
                return Ease(0f, wrapTarget, progress - 1f);
            }

            return Ease(from, to, progress);
        }

        private static float Ease(float start, float end, float progress)
        {
            if (progress >= 1f)
            {
                return end;
            }

            var clamped = Mathf.Clamp01(progress);
            var eased = 1f - ((1f - clamped) * (1f - clamped));
            return Mathf.Lerp(start, end, eased);
        }
    }
}
