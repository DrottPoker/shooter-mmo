using AuthService.Auth;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AccountValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void ValidateEmailRejectsInvalidValues(string? email)
    {
        Assert.NotNull(AccountValidation.ValidateEmail(email));
    }

    [Fact]
    public void ValidateEmailAcceptsAStandardAddress()
    {
        Assert.Null(AccountValidation.ValidateEmail("player@example.com"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("player-name")]
    [InlineData("player name")]
    public void ValidateUsernameRejectsInvalidValues(string username)
    {
        Assert.NotNull(AccountValidation.ValidateUsername(username));
    }

    [Theory]
    [InlineData("player")]
    [InlineData("player_123")]
    public void ValidateUsernameAcceptsSupportedValues(string username)
    {
        Assert.Null(AccountValidation.ValidateUsername(username));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("        ")]
    public void ValidatePasswordRejectsInvalidValues(string? password)
    {
        Assert.NotNull(AccountValidation.ValidatePassword(password));
    }

    [Fact]
    public void ValidatePasswordAcceptsAValidValue()
    {
        Assert.Null(AccountValidation.ValidatePassword("TestPass123!"));
    }
}
