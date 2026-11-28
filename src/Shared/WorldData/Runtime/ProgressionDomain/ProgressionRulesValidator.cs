using System;

namespace Mmorpg.WorldData.Progression
{
    public static class ProgressionRulesValidator
    {
        public static void Validate(ProgressionRulesDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.formatVersion != ProgressionRulesFormat.Version)
            {
                throw new ProgressionRulesValidationException("Progression rules format version is unsupported.");
            }

            ValidateInteger(
                document.maximumCharacterLevel,
                1,
                ProgressionRulesFormat.MaximumCharacterLevel,
                "MaximumCharacterLevel");

            var curve = document.levelCurve
                ?? throw new ProgressionRulesValidationException("LevelCurve rules are required.");
            var mob = document.mobExperience
                ?? throw new ProgressionRulesValidationException("MobExperience rules are required.");
            var difference = document.levelDifference
                ?? throw new ProgressionRulesValidationException("LevelDifference rules are required.");

            if (!IsFinite(curve.killsAtLevelOne)
                || curve.killsAtLevelOne <= 0d
                || curve.killsAtLevelOne > ProgressionRulesFormat.MaximumKillsAtLevelOne
                || ToDecimal(curve.killsAtLevelOne) <= 0m)
            {
                throw new ProgressionRulesValidationException(
                    "LevelCurve.KillsAtLevelOne must be greater than 0 and at most "
                    + ProgressionRulesFormat.MaximumKillsAtLevelOne
                    + ".");
            }

            if (!IsFinite(curve.additionalKillsPerLevel)
                || curve.additionalKillsPerLevel < 0d
                || curve.additionalKillsPerLevel > ProgressionRulesFormat.MaximumAdditionalKillsPerLevel)
            {
                throw new ProgressionRulesValidationException(
                    "LevelCurve.AdditionalKillsPerLevel must be between 0 and "
                    + ProgressionRulesFormat.MaximumAdditionalKillsPerLevel
                    + ".");
            }

            ValidateInteger(
                curve.roundExperienceTo,
                1,
                ProgressionRulesFormat.MaximumRoundExperienceTo,
                "LevelCurve.RoundExperienceTo");
            ValidateInteger(
                mob.atLevelOne,
                1,
                ProgressionRulesFormat.MaximumMobExperienceAtLevelOne,
                "MobExperience.AtLevelOne");
            ValidateInteger(
                mob.perLevel,
                0,
                ProgressionRulesFormat.MaximumMobExperiencePerLevel,
                "MobExperience.PerLevel");

            ValidateInteger(
                difference.minimumLevels,
                0,
                ProgressionRulesFormat.MaximumCharacterLevel,
                "LevelDifference.MinimumLevels");
            ValidateInteger(
                difference.higherLevelsCountedPercent,
                0,
                ProgressionRulesFormat.MaximumLevelDifferencePercent,
                "LevelDifference.HigherLevelsCountedPercent");
            ValidateInteger(
                difference.fullExperienceBelowPercent,
                0,
                ProgressionRulesFormat.MaximumLevelDifferencePercent,
                "LevelDifference.FullExperienceBelowPercent");
            ValidateInteger(
                difference.noExperienceBelowPercent,
                0,
                ProgressionRulesFormat.MaximumLevelDifferencePercent,
                "LevelDifference.NoExperienceBelowPercent");
            if (difference.fullExperienceBelowPercent > difference.noExperienceBelowPercent)
            {
                throw new ProgressionRulesValidationException(
                    "LevelDifference.FullExperienceBelowPercent must not exceed LevelDifference.NoExperienceBelowPercent.");
            }

            // Both the kills and the Mob experience grow with the level, so the last level that
            // needs experience needs the most.
            if (document.maximumCharacterLevel > 1)
            {
                var lastLevel = document.maximumCharacterLevel - 1;
                var experience = ProgressionRules.LevelExperience(
                    ToDecimal(curve.killsAtLevelOne),
                    ToDecimal(curve.additionalKillsPerLevel),
                    curve.roundExperienceTo,
                    mob.atLevelOne,
                    mob.perLevel,
                    lastLevel);
                if (experience > ProgressionRulesFormat.MaximumExperienceToNextLevel)
                {
                    throw new ProgressionRulesValidationException(
                        "LevelCurve: level "
                        + lastLevel
                        + " needs "
                        + experience
                        + " experience, more than the allowed "
                        + ProgressionRulesFormat.MaximumExperienceToNextLevel
                        + ".");
                }
            }
        }

        // Authored decimals such as 0.75 become exact decimal values.
        public static decimal ToDecimal(double value)
        {
            return Math.Round((decimal)value, 4);
        }

        private static void ValidateInteger(int value, int minimum, int maximum, string name)
        {
            if (value < minimum || value > maximum)
            {
                throw new ProgressionRulesValidationException(
                    name + " must be between " + minimum + " and " + maximum + ".");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
