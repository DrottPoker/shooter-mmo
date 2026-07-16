using AuthService.Items;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class DevelopmentItemToolCommandParserTests
{
    [Fact]
    public void UnrelatedArgumentsDoNotActivateCommandMode()
    {
        var activated = DevelopmentItemToolCommandParser.TryParse(
            ["--urls", "http://127.0.0.1:5100"],
            out var command,
            out var error);

        Assert.False(activated);
        Assert.Null(command);
        Assert.Empty(error);
    }

    [Fact]
    public void ListCommandActivatesWithoutACharacter()
    {
        var activated = DevelopmentItemToolCommandParser.TryParse(
            ["--dev-items-list"],
            out var command,
            out var error);

        Assert.True(activated);
        Assert.NotNull(command);
        Assert.Equal(DevelopmentItemToolCommandKind.List, command.Kind);
        Assert.Empty(error);
    }

    [Fact]
    public void GrantCommandParsesAllAuthoritativeInputs()
    {
        var characterId = Guid.NewGuid();

        var activated = DevelopmentItemToolCommandParser.TryParse(
            [
                "--dev-items-grant",
                characterId.ToString(),
                "material.iron_ore",
                "17",
                "bank"
            ],
            out var command,
            out var error);

        Assert.True(activated);
        Assert.NotNull(command);
        Assert.Equal(DevelopmentItemToolCommandKind.Grant, command.Kind);
        Assert.Equal(characterId, command.CharacterId);
        Assert.Equal("material.iron_ore", command.DefinitionId);
        Assert.Equal(17, command.Quantity);
        Assert.Equal(DevelopmentItemDestination.Bank, command.Destination);
        Assert.Empty(error);
    }

    [Fact]
    public void PackageCommandParsesPackageIdentity()
    {
        var characterId = Guid.NewGuid();

        var activated = DevelopmentItemToolCommandParser.TryParse(
            ["--dev-items-package", characterId.ToString(), "encumbrance_140"],
            out var command,
            out var error);

        Assert.True(activated);
        Assert.NotNull(command);
        Assert.Equal(DevelopmentItemToolCommandKind.Package, command.Kind);
        Assert.Equal(characterId, command.CharacterId);
        Assert.Equal("encumbrance_140", command.PackageId);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("missing", "1", "bank")]
    [InlineData("00000000-0000-0000-0000-000000000000", "1", "bank")]
    [InlineData("73f78973-bda2-4e7d-a6d1-d6247606bd4e", "0", "bank")]
    [InlineData("73f78973-bda2-4e7d-a6d1-d6247606bd4e", "1", "recovery")]
    public void InvalidGrantArgumentsReturnStableUsage(
        string characterId,
        string quantity,
        string destination)
    {
        var activated = DevelopmentItemToolCommandParser.TryParse(
            [
                "--dev-items-grant",
                characterId,
                "material.iron_ore",
                quantity,
                destination
            ],
            out var command,
            out var error);

        Assert.True(activated);
        Assert.Null(command);
        Assert.Equal(
            "--dev-items-grant requires <characterGuid> <definitionId> "
                + "<positiveQuantity> <permanent|bank|secure>.",
            error);
    }

    [Fact]
    public void MultipleDevelopmentCommandsAreRejected()
    {
        var activated = DevelopmentItemToolCommandParser.TryParse(
            ["--dev-items-list", "--dev-items-package", Guid.NewGuid().ToString(), "equipment"],
            out var command,
            out var error);

        Assert.True(activated);
        Assert.Null(command);
        Assert.Equal("Exactly one development item tool command may be supplied.", error);
    }
}
