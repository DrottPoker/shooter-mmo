using Mmorpg.Server.Common.Progression;
using Mmorpg.Testing;

namespace Mmorpg.Server.Common.Tests;

public sealed class ProgressionRulesFileLoaderTests
{
    private const string ValidJson = """
        {
          "formatVersion": 1,
          "maximumCharacterLevel": 50,
          "levelCurve": { "killsAtLevelOne": 2.5, "additionalKillsPerLevel": 0.75, "roundExperienceTo": 5 },
          "mobExperience": { "atLevelOne": 10, "perLevel": 2 },
          "levelDifference": {
            "minimumLevels": 3,
            "higherLevelsCountedPercent": 20,
            "fullExperienceBelowPercent": 20,
            "noExperienceBelowPercent": 40
          }
        }
        """;

    [Fact]
    public void CheckedInRulesLoad()
    {
        var rules = ProgressionRulesFileLoader.Load(RepositoryPaths.ProgressionRulesPath);

        Assert.Equal(200, rules.MaximumCharacterLevel);
        Assert.Equal(100, rules.ExperienceToNextLevel(1));
    }

    [Fact]
    public void AuthoredDecimalsAreReadExactly()
    {
        var rules = ProgressionRulesFileLoader.Parse(ValidJson);

        Assert.Equal(50, rules.MaximumCharacterLevel);
        // Level 2: 3.25 kills of 12 experience is 39, rounded to 40. Level 3: 4 kills of 14 is
        // 56, rounded to 55.
        Assert.Equal(40, rules.ExperienceToNextLevel(2));
        Assert.Equal(55, rules.ExperienceToNextLevel(3));
    }

    [Fact]
    public void ARelativePathResolvesNextToTheRunningAssembly()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ProgressionRulesFileLoader.DefaultRelativePath)),
            ProgressionRulesFileLoader.ResolvePath(null));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("{", "malformed")]
    [InlineData("""{ "formatVersion": 1, "formatVersion": 1 }""", "duplicate property 'formatVersion' at $")]
    [InlineData("""{ "formatVersion": 1, "levelCurve": { "roundExperienceTo": 1, "roundExperienceTo": 1 } }""", "duplicate property 'roundExperienceTo' at $.levelCurve")]
    [InlineData("""{ "formatVersion": 1, "experienceCurve": {} }""", "malformed")]
    [InlineData("""{ "formatVersion": 1, "levelCurve": { "killsPerLevel": 1 } }""", "malformed")]
    [InlineData("""{ "formatVersion": 1, "maximumCharacterLevel": 200, "MaximumCharacterLevel": 200 }""", "malformed")]
    [InlineData("""{ "formatVersion": 1 } // comment""", "malformed")]
    [InlineData("""{ "formatVersion": 1, }""", "malformed")]
    [InlineData("""{ "formatVersion": 1, "maximumCharacterLevel": 0 }""", "invalid: MaximumCharacterLevel")]
    [InlineData("""{ "formatVersion": 2 }""", "invalid: Progression rules format version")]
    [InlineData("""{ "formatVersion": 1, "maximumCharacterLevel": 200, "levelCurve": null }""", "invalid: LevelCurve rules are required")]
    public void BrokenRulesAreRejected(string json, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ProgressionRulesFileLoader.Parse(json));
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("The progression rules", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRulesAreRejected()
    {
        Assert.Throws<FileNotFoundException>(() => ProgressionRulesFileLoader.Load(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "progression-rules.json")));
    }
}
