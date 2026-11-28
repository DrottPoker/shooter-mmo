using System;
using Mmorpg.WorldData.Combat;

namespace Mmorpg.WorldData.Progression
{
    public static class ProgressionRulesFormat
    {
        public const int Version = 1;
        public const string FileName = "progression-rules.json";
        public const string DirectoryName = "progression";

        public const int MaximumCharacterLevel = CombatContentRules.MaximumCharacterLevel;
        public const double MaximumKillsAtLevelOne = 100_000d;
        public const double MaximumAdditionalKillsPerLevel = 10_000d;
        public const int MaximumRoundExperienceTo = 1_000_000;
        public const int MaximumMobExperienceAtLevelOne = 1_000_000;
        public const int MaximumMobExperiencePerLevel = 1_000_000;
        public const int MaximumLevelDifferencePercent = 10_000;

        // A Mob's experience percent: 100 is the plain value of its level, 0 gives nothing.
        public const int DefaultMobExperiencePercent = 100;
        public const int MaximumMobExperiencePercent = 100_000;

        // No level may need more than this, so sums of experience stay far inside a long.
        public const long MaximumExperienceToNextLevel = 1_000_000_000_000L;
    }

    // The tunable rules of character levelling: the highest level, how much experience each
    // level needs, how much a Mob of each level gives, and how a level difference between a
    // character and a Mob changes that. GameService reads this file and owns every result;
    // the worker and the client only show what GameService computed.
    [Serializable]
    public sealed class ProgressionRulesDocument
    {
        public int formatVersion;

        // Experience stops at this level: a character at it gains none.
        public int maximumCharacterLevel;

        public LevelCurveRulesDocument levelCurve = new LevelCurveRulesDocument();
        public MobExperienceRulesDocument mobExperience = new MobExperienceRulesDocument();
        public LevelDifferenceRulesDocument levelDifference = new LevelDifferenceRulesDocument();
    }

    // A level needs as much experience as a number of Mobs of that level give. The number
    // grows by the same amount every level, which keeps the time per level a straight line.
    [Serializable]
    public sealed class LevelCurveRulesDocument
    {
        // Mobs of level 1 that level 1 needs.
        public double killsAtLevelOne;

        // Each level needs this many more Mobs of its own level than the level before.
        public double additionalKillsPerLevel;

        // The experience a level needs is rounded to a multiple of this.
        public int roundExperienceTo;
    }

    // A Mob gives experience by its own level, before its own percent and the level difference.
    [Serializable]
    public sealed class MobExperienceRulesDocument
    {
        public int atLevelOne;
        public int perLevel;
    }

    // Wide rails around the character's level. Strength sits in gear, so a Mob far from the
    // character's level is still a real fight: the rails only stop a low character from being
    // carried by high Mobs and a high character from farming Mobs that cannot hurt it.
    [Serializable]
    public sealed class LevelDifferenceRulesDocument
    {
        // Every distance below is at least this many levels, so the rails work at low levels.
        public int minimumLevels;

        // A Mob counts as at most this percent of the character's level above the character.
        public int higherLevelsCountedPercent;

        // A Mob gives its full experience down to this percent of the character's level below
        // the character.
        public int fullExperienceBelowPercent;

        // From there the experience falls in a straight line to nothing at this percent of the
        // character's level below the character.
        public int noExperienceBelowPercent;
    }

    public sealed class ProgressionRulesValidationException : Exception
    {
        public ProgressionRulesValidationException(string message)
            : base(message)
        {
        }
    }

    // A character's level and the experience it has gathered inside that level.
    public readonly struct ProgressionState : IEquatable<ProgressionState>
    {
        public ProgressionState(int level, long experience)
        {
            Level = level;
            Experience = experience;
        }

        public int Level { get; }

        // Experience toward the next level, never the total since level 1. A change of the
        // curve therefore never changes a level that a character already has.
        public long Experience { get; }

        public bool Equals(ProgressionState other)
        {
            return Level == other.Level && Experience == other.Experience;
        }

        public override bool Equals(object obj)
        {
            return obj is ProgressionState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Level * 397) ^ Experience.GetHashCode();
        }
    }

    // The validated rules with the arithmetic. All results are whole numbers and every step
    // uses decimal, so the same rules give the same result on every machine.
    public sealed class ProgressionRules
    {
        private readonly decimal killsAtLevelOne;
        private readonly decimal additionalKillsPerLevel;
        private readonly int roundExperienceTo;
        private readonly int mobExperienceAtLevelOne;
        private readonly int mobExperiencePerLevel;
        private readonly int minimumLevels;
        private readonly int higherLevelsCountedPercent;
        private readonly int fullExperienceBelowPercent;
        private readonly int noExperienceBelowPercent;

        private ProgressionRules(ProgressionRulesDocument document)
        {
            MaximumCharacterLevel = document.maximumCharacterLevel;
            killsAtLevelOne = ProgressionRulesValidator.ToDecimal(document.levelCurve.killsAtLevelOne);
            additionalKillsPerLevel = ProgressionRulesValidator.ToDecimal(document.levelCurve.additionalKillsPerLevel);
            roundExperienceTo = document.levelCurve.roundExperienceTo;
            mobExperienceAtLevelOne = document.mobExperience.atLevelOne;
            mobExperiencePerLevel = document.mobExperience.perLevel;
            minimumLevels = document.levelDifference.minimumLevels;
            higherLevelsCountedPercent = document.levelDifference.higherLevelsCountedPercent;
            fullExperienceBelowPercent = document.levelDifference.fullExperienceBelowPercent;
            noExperienceBelowPercent = document.levelDifference.noExperienceBelowPercent;
        }

        public int MaximumCharacterLevel { get; }

        public static ProgressionRules FromDocument(ProgressionRulesDocument document)
        {
            ProgressionRulesValidator.Validate(document);
            return new ProgressionRules(document);
        }

        // What a Mob of this level gives before its own percent and the level difference.
        public long MobBaseExperience(int mobLevel)
        {
            return MobBaseExperience(mobExperienceAtLevelOne, mobExperiencePerLevel, mobLevel);
        }

        // The experience that takes a character from this level to the next: 0 at the highest
        // level, where experience stops.
        public long ExperienceToNextLevel(int level)
        {
            if (level >= MaximumCharacterLevel)
            {
                return 0;
            }

            return LevelExperience(
                killsAtLevelOne,
                additionalKillsPerLevel,
                roundExperienceTo,
                mobExperienceAtLevelOne,
                mobExperiencePerLevel,
                level);
        }

        // The formulas on raw values, so the validator checks the same numbers without rules.
        internal static long MobBaseExperience(int atLevelOne, int perLevel, int mobLevel)
        {
            var level = Math.Max(1, mobLevel);
            return atLevelOne + ((long)perLevel * (level - 1));
        }

        internal static long LevelExperience(
            decimal killsAtLevelOne,
            decimal additionalKillsPerLevel,
            int roundExperienceTo,
            int mobExperienceAtLevelOne,
            int mobExperiencePerLevel,
            int level)
        {
            var current = Math.Max(1, level);
            var kills = killsAtLevelOne + (additionalKillsPerLevel * (current - 1));
            var exact = kills * MobBaseExperience(mobExperienceAtLevelOne, mobExperiencePerLevel, current);
            var rounded = Math.Round(exact / roundExperienceTo, MidpointRounding.AwayFromZero) * roundExperienceTo;
            return Math.Max(roundExperienceTo, (long)rounded);
        }

        // The highest Mob level that still counts as itself for this character. A higher Mob
        // gives what a Mob of this level gives.
        public int HighestCountedMobLevel(int characterLevel)
        {
            var level = Math.Max(1, characterLevel);
            return level + Distance(level, higherLevelsCountedPercent);
        }

        // A Mob at or below this level gives this character no experience. Below level 1 when
        // every Mob still gives some.
        public int HighestMobLevelWithoutExperience(int characterLevel)
        {
            var level = Math.Max(1, characterLevel);
            var full = FullExperienceDistance(level);
            var none = NoExperienceDistance(level);

            // With no falling stretch between the two distances, the full distance still counts.
            return none > full ? level - none : level - none - 1;
        }

        // What a character of this level gets for the kill of a Mob. experiencePercent is the
        // Mob's own percent from its definition.
        public long KillExperience(int characterLevel, int mobLevel, int experiencePercent)
        {
            var level = Math.Max(1, characterLevel);
            if (level >= MaximumCharacterLevel || experiencePercent <= 0)
            {
                return 0;
            }

            var mob = Math.Max(1, mobLevel);
            var counted = Math.Min(mob, HighestCountedMobLevel(level));
            var plain = MobBaseExperience(counted) * (decimal)experiencePercent / 100m;

            var below = level - mob;
            var full = FullExperienceDistance(level);
            var none = NoExperienceDistance(level);
            decimal share;
            if (below <= full)
            {
                share = 1m;
            }
            else if (below >= none)
            {
                return 0;
            }
            else
            {
                share = (decimal)(none - below) / (none - full);
            }

            // A kill that counts at all gives at least one point.
            return Math.Max(1L, (long)Math.Floor(plain * share));
        }

        // Adds gained experience and levels up as often as it reaches. At the highest level the
        // state holds no experience and nothing is added.
        public ProgressionState Add(ProgressionState state, long gained)
        {
            var level = Math.Min(Math.Max(1, state.Level), MaximumCharacterLevel);
            if (level >= MaximumCharacterLevel)
            {
                return new ProgressionState(level, 0);
            }

            var experience = Math.Max(0, state.Experience) + Math.Max(0, gained);
            while (level < MaximumCharacterLevel)
            {
                var needed = ExperienceToNextLevel(level);
                if (experience < needed)
                {
                    return new ProgressionState(level, experience);
                }

                experience -= needed;
                level++;
            }

            return new ProgressionState(level, 0);
        }

        private int FullExperienceDistance(int level)
        {
            return Distance(level, fullExperienceBelowPercent);
        }

        private int NoExperienceDistance(int level)
        {
            return Math.Max(FullExperienceDistance(level), Distance(level, noExperienceBelowPercent));
        }

        private int Distance(int level, int percent)
        {
            return Math.Max(minimumLevels, (int)((long)level * percent / 100));
        }
    }
}
