using AuthService.Auth;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class TokenGeneratorTests
{
    [Fact]
    public void CreateTokenProducesUniqueBase64UrlValues()
    {
        var first = TokenGenerator.CreateToken();
        var second = TokenGenerator.CreateToken();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("+", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/", first, StringComparison.Ordinal);
        Assert.DoesNotContain("=", first, StringComparison.Ordinal);
    }

    [Fact]
    public void HashTokenIsDeterministicAndDoesNotReturnTheRawToken()
    {
        const string token = "test-token";

        var firstHash = TokenGenerator.HashToken(token);
        var secondHash = TokenGenerator.HashToken(token);

        Assert.Equal(firstHash, secondHash);
        Assert.NotEqual(token, firstHash);
        Assert.Equal(64, firstHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", firstHash);
    }
}
