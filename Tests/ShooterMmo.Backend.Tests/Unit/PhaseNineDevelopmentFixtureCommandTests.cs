using AuthService.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class PhaseNineDevelopmentFixtureCommandTests
{
    [Fact]
    public void MissingFixtureArgumentDoesNotActivateCommandMode()
    {
        var activated = PhaseNineDevelopmentFixtureCommand.TryParse(
            ["--urls", "http://127.0.0.1:5100"],
            out var characterId,
            out var error);

        Assert.False(activated);
        Assert.Equal(Guid.Empty, characterId);
        Assert.Empty(error);
    }

    [Fact]
    public void ValidFixtureArgumentReturnsCharacterId()
    {
        var expectedCharacterId = Guid.NewGuid();

        var activated = PhaseNineDevelopmentFixtureCommand.TryParse(
            ["--seed-phase9-items", expectedCharacterId.ToString()],
            out var characterId,
            out var error);

        Assert.True(activated);
        Assert.Equal(expectedCharacterId, characterId);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void InvalidFixtureArgumentReturnsStableValidationError(string value)
    {
        var activated = PhaseNineDevelopmentFixtureCommand.TryParse(
            ["--seed-phase9-items", value],
            out var characterId,
            out var error);

        Assert.True(activated);
        Assert.Equal(Guid.Empty, characterId);
        Assert.Equal(
            "--seed-phase9-items requires a non-empty character GUID.",
            error);
    }

    [Fact]
    public void DuplicateFixtureArgumentIsRejected()
    {
        var characterId = Guid.NewGuid();

        var activated = PhaseNineDevelopmentFixtureCommand.TryParse(
            [
                "--seed-phase9-items",
                characterId.ToString(),
                "--seed-phase9-items",
                characterId.ToString()
            ],
            out var parsedCharacterId,
            out var error);

        Assert.True(activated);
        Assert.Equal(Guid.Empty, parsedCharacterId);
        Assert.Equal("--seed-phase9-items may be supplied only once.", error);
    }
}
