using Mmorpg.Testing;
using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Progression;

namespace Mmorpg.Shared.Tests;

// The checked-in progression rules give exactly the numbers of the design, and the validator
// refuses every value outside its rules.
public sealed class ProgressionRulesTests
{
    private static readonly Lazy<ProgressionRules> Rules = new(() =>
        ProgressionRules.FromDocument(LoadDocument()));

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 150)]
    [InlineData(10, 910)]
    [InlineData(50, 14310)]
    [InlineData(100, 53560)]
    [InlineData(199, 205030)]
    [InlineData(200, 0)]
    public void ExperienceToNextLevelFollowsTheCurve(int level, long experience)
    {
        Assert.Equal(experience, Rules.Value.ExperienceToNextLevel(level));
    }

    [Fact]
    public void TheHighestLevelNeedsTheSumOfEveryLevelBelowIt()
    {
        var total = 0L;
        for (var level = 1; level <= 199; level++)
        {
            total += Rules.Value.ExperienceToNextLevel(level);
        }

        Assert.Equal(200, Rules.Value.MaximumCharacterLevel);
        Assert.Equal(13941940L, total);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(87, 450)]
    [InlineData(200, 1015)]
    public void MobBaseExperienceGrowsByLevel(int mobLevel, long experience)
    {
        Assert.Equal(experience, Rules.Value.MobBaseExperience(mobLevel));
    }

    [Fact]
    public void TheRailsOfALevel87CharacterSpanAQuarterAboveAndHalfBelow()
    {
        Assert.Equal(108, Rules.Value.HighestCountedMobLevel(87));
        Assert.Equal(44, Rules.Value.HighestMobLevelWithoutExperience(87));
    }

    [Theory]
    [InlineData(87, 87, 100, 450)]
    [InlineData(87, 66, 100, 345)]
    [InlineData(87, 65, 100, 324)]
    [InlineData(87, 44, 100, 0)]
    [InlineData(87, 87, 500, 2250)]
    [InlineData(87, 87, 0, 0)]
    [InlineData(7, 2, 100, 25)]
    [InlineData(7, 1, 100, 0)]
    public void KillExperienceFollowsTheLevelDifference(int characterLevel, int mobLevel, int percent, long experience)
    {
        Assert.Equal(experience, Rules.Value.KillExperience(characterLevel, mobLevel, percent));
    }

    [Fact]
    public void AMobAboveTheCountedLevelsGivesWhatTheHighestCountedLevelGives()
    {
        Assert.Equal(555L, Rules.Value.MobBaseExperience(108));
        Assert.Equal(Rules.Value.MobBaseExperience(108), Rules.Value.KillExperience(87, 150, 100));
    }

    [Fact]
    public void TheFallingStretchGivesAShareOfThePlainExperience()
    {
        // 22 levels below a level 87 character: a share of (43 - 22) / (43 - 21) of 340.
        Assert.Equal(340L, Rules.Value.MobBaseExperience(65));
        Assert.Equal((long)Math.Floor(340m * 21m / 22m), Rules.Value.KillExperience(87, 65, 100));
    }

    [Fact]
    public void AtLowLevelsTheMinimumDistanceHoldsTheRails()
    {
        Assert.Equal(1, Rules.Value.HighestMobLevelWithoutExperience(7));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(250)]
    public void AtTheHighestLevelEveryKillGivesNothing(int mobLevel)
    {
        Assert.Equal(0L, Rules.Value.KillExperience(200, mobLevel, 100));
        Assert.Equal(0L, Rules.Value.KillExperience(200, mobLevel, 500));
    }

    [Theory]
    [InlineData(1, 0, 100, 2, 0)]
    [InlineData(1, 0, 99, 1, 99)]
    [InlineData(1, 0, 250, 3, 0)]
    [InlineData(1, 0, long.MaxValue / 2, 200, 0)]
    [InlineData(200, 0, 1000, 200, 0)]
    [InlineData(5, 30, -50, 5, 30)]
    public void AddLevelsUpAsOftenAsTheExperienceReaches(
        int level,
        long experience,
        long gained,
        int expectedLevel,
        long expectedExperience)
    {
        Assert.Equal(
            new ProgressionState(expectedLevel, expectedExperience),
            Rules.Value.Add(new ProgressionState(level, experience), gained));
    }

    [Fact]
    public void AuthoredDecimalsBecomeExact()
    {
        Assert.Equal(0.75m, ProgressionRulesValidator.ToDecimal(0.75d));
        Assert.Equal(0.3m, ProgressionRulesValidator.ToDecimal(0.1d + 0.2d));
    }

    [Fact]
    public void TheValidatorAcceptsTheCheckedInRulesAndAValidDocument()
    {
        ProgressionRulesValidator.Validate(LoadDocument());
        ProgressionRulesValidator.Validate(ValidDocument());
    }

    [Fact]
    public void TheValidatorRejectsAMissingDocument()
    {
        Assert.Throws<ArgumentNullException>(() => ProgressionRulesValidator.Validate(null!));
    }

    [Theory]
    [InlineData("format-version", "format version is unsupported")]
    [InlineData("maximum-level-zero", "MaximumCharacterLevel must be between 1 and 10000")]
    [InlineData("maximum-level-above-limit", "MaximumCharacterLevel must be between 1 and 10000")]
    [InlineData("missing-level-curve", "LevelCurve rules are required")]
    [InlineData("missing-mob-experience", "MobExperience rules are required")]
    [InlineData("missing-level-difference", "LevelDifference rules are required")]
    [InlineData("kills-zero", "LevelCurve.KillsAtLevelOne must be greater than 0")]
    [InlineData("kills-rounding-to-zero", "LevelCurve.KillsAtLevelOne must be greater than 0")]
    [InlineData("kills-nan", "LevelCurve.KillsAtLevelOne must be greater than 0")]
    [InlineData("kills-infinite", "LevelCurve.KillsAtLevelOne must be greater than 0")]
    [InlineData("kills-above-limit", "LevelCurve.KillsAtLevelOne must be greater than 0")]
    [InlineData("additional-kills-negative", "LevelCurve.AdditionalKillsPerLevel must be between 0")]
    [InlineData("additional-kills-nan", "LevelCurve.AdditionalKillsPerLevel must be between 0")]
    [InlineData("additional-kills-above-limit", "LevelCurve.AdditionalKillsPerLevel must be between 0")]
    [InlineData("round-zero", "LevelCurve.RoundExperienceTo must be between 1")]
    [InlineData("round-above-limit", "LevelCurve.RoundExperienceTo must be between 1")]
    [InlineData("mob-at-level-one-zero", "MobExperience.AtLevelOne must be between 1")]
    [InlineData("mob-at-level-one-above-limit", "MobExperience.AtLevelOne must be between 1")]
    [InlineData("mob-per-level-negative", "MobExperience.PerLevel must be between 0")]
    [InlineData("mob-per-level-above-limit", "MobExperience.PerLevel must be between 0")]
    [InlineData("minimum-levels-negative", "LevelDifference.MinimumLevels must be between 0")]
    [InlineData("minimum-levels-above-limit", "LevelDifference.MinimumLevels must be between 0")]
    [InlineData("higher-percent-negative", "LevelDifference.HigherLevelsCountedPercent must be between 0")]
    [InlineData("higher-percent-above-limit", "LevelDifference.HigherLevelsCountedPercent must be between 0")]
    [InlineData("full-percent-negative", "LevelDifference.FullExperienceBelowPercent must be between 0")]
    [InlineData("full-percent-above-limit", "LevelDifference.FullExperienceBelowPercent must be between 0")]
    [InlineData("none-percent-negative", "LevelDifference.NoExperienceBelowPercent must be between 0")]
    [InlineData("none-percent-above-limit", "LevelDifference.NoExperienceBelowPercent must be between 0")]
    [InlineData("full-above-none", "FullExperienceBelowPercent must not exceed")]
    [InlineData("last-level-too-expensive", "LevelCurve: level 199 needs")]
    public void TheValidatorRejectsAValueOutsideItsRule(string mutation, string error)
    {
        var document = ValidDocument();
        Mutate(document, mutation);

        var exception = Assert.Throws<ProgressionRulesValidationException>(
            () => ProgressionRulesValidator.Validate(document));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
        Assert.Throws<ProgressionRulesValidationException>(() => ProgressionRules.FromDocument(document));
    }

    [Fact]
    public void TheMostExpensiveCurveStillPassesWhenOnlyItsFirstLevelNeedsExperience()
    {
        var document = ValidDocument();
        Mutate(document, "last-level-too-expensive");
        document.maximumCharacterLevel = 2;

        ProgressionRulesValidator.Validate(document);
    }

    [Fact]
    public void TheFullExperiencePercentMayEqualTheNoExperiencePercent()
    {
        var document = ValidDocument();
        document.levelDifference.fullExperienceBelowPercent = document.levelDifference.noExperienceBelowPercent;

        ProgressionRulesValidator.Validate(document);
    }

    private static void Mutate(ProgressionRulesDocument document, string mutation)
    {
        var curve = document.levelCurve;
        var mob = document.mobExperience;
        var difference = document.levelDifference;
        switch (mutation)
        {
            case "format-version":
                document.formatVersion = ProgressionRulesFormat.Version + 1;
                break;
            case "maximum-level-zero":
                document.maximumCharacterLevel = 0;
                break;
            case "maximum-level-above-limit":
                document.maximumCharacterLevel = ProgressionRulesFormat.MaximumCharacterLevel + 1;
                break;
            case "missing-level-curve":
                document.levelCurve = null!;
                break;
            case "missing-mob-experience":
                document.mobExperience = null!;
                break;
            case "missing-level-difference":
                document.levelDifference = null!;
                break;
            case "kills-zero":
                curve.killsAtLevelOne = 0d;
                break;
            case "kills-rounding-to-zero":
                curve.killsAtLevelOne = 0.00001d;
                break;
            case "kills-nan":
                curve.killsAtLevelOne = double.NaN;
                break;
            case "kills-infinite":
                curve.killsAtLevelOne = double.PositiveInfinity;
                break;
            case "kills-above-limit":
                curve.killsAtLevelOne = ProgressionRulesFormat.MaximumKillsAtLevelOne + 1d;
                break;
            case "additional-kills-negative":
                curve.additionalKillsPerLevel = -0.5d;
                break;
            case "additional-kills-nan":
                curve.additionalKillsPerLevel = double.NaN;
                break;
            case "additional-kills-above-limit":
                curve.additionalKillsPerLevel = ProgressionRulesFormat.MaximumAdditionalKillsPerLevel + 1d;
                break;
            case "round-zero":
                curve.roundExperienceTo = 0;
                break;
            case "round-above-limit":
                curve.roundExperienceTo = ProgressionRulesFormat.MaximumRoundExperienceTo + 1;
                break;
            case "mob-at-level-one-zero":
                mob.atLevelOne = 0;
                break;
            case "mob-at-level-one-above-limit":
                mob.atLevelOne = ProgressionRulesFormat.MaximumMobExperienceAtLevelOne + 1;
                break;
            case "mob-per-level-negative":
                mob.perLevel = -1;
                break;
            case "mob-per-level-above-limit":
                mob.perLevel = ProgressionRulesFormat.MaximumMobExperiencePerLevel + 1;
                break;
            case "minimum-levels-negative":
                difference.minimumLevels = -1;
                break;
            case "minimum-levels-above-limit":
                difference.minimumLevels = ProgressionRulesFormat.MaximumCharacterLevel + 1;
                break;
            case "higher-percent-negative":
                difference.higherLevelsCountedPercent = -1;
                break;
            case "higher-percent-above-limit":
                difference.higherLevelsCountedPercent = ProgressionRulesFormat.MaximumLevelDifferencePercent + 1;
                break;
            case "full-percent-negative":
                difference.fullExperienceBelowPercent = -1;
                break;
            case "full-percent-above-limit":
                difference.fullExperienceBelowPercent = ProgressionRulesFormat.MaximumLevelDifferencePercent + 1;
                break;
            case "none-percent-negative":
                difference.noExperienceBelowPercent = -1;
                break;
            case "none-percent-above-limit":
                difference.noExperienceBelowPercent = ProgressionRulesFormat.MaximumLevelDifferencePercent + 1;
                break;
            case "full-above-none":
                difference.fullExperienceBelowPercent = difference.noExperienceBelowPercent + 1;
                break;
            case "last-level-too-expensive":
                // Every value is inside its own limit, but level 199 would need about 4 * 10^14.
                curve.killsAtLevelOne = ProgressionRulesFormat.MaximumKillsAtLevelOne;
                curve.additionalKillsPerLevel = ProgressionRulesFormat.MaximumAdditionalKillsPerLevel;
                mob.atLevelOne = ProgressionRulesFormat.MaximumMobExperienceAtLevelOne;
                mob.perLevel = ProgressionRulesFormat.MaximumMobExperiencePerLevel;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static ProgressionRulesDocument LoadDocument()
    {
        return StrictContentJson.DeserializeFile<ProgressionRulesDocument>(
            RepositoryPaths.ProgressionRulesPath,
            "Progression rules");
    }

    private static ProgressionRulesDocument ValidDocument()
    {
        return new ProgressionRulesDocument
        {
            formatVersion = ProgressionRulesFormat.Version,
            maximumCharacterLevel = 200,
            levelCurve = new LevelCurveRulesDocument
            {
                killsAtLevelOne = 5d,
                additionalKillsPerLevel = 1d,
                roundExperienceTo = 10
            },
            mobExperience = new MobExperienceRulesDocument
            {
                atLevelOne = 20,
                perLevel = 5
            },
            levelDifference = new LevelDifferenceRulesDocument
            {
                minimumLevels = 5,
                higherLevelsCountedPercent = 25,
                fullExperienceBelowPercent = 25,
                noExperienceBelowPercent = 50
            }
        };
    }
}
