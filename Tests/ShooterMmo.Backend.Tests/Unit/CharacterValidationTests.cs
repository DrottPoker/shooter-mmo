using AuthService.Characters;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class CharacterValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("Hero  One")]
    [InlineData("Hero!")]
    public void ValidateNameRejectsInvalidValues(string? name)
    {
        Assert.NotNull(CharacterValidation.ValidateName(name));
    }

    [Theory]
    [InlineData("Hero One")]
    [InlineData("Hero-One")]
    [InlineData("Hero_123")]
    public void ValidateNameAcceptsSupportedValues(string name)
    {
        Assert.Null(CharacterValidation.ValidateName(name));
    }
}
