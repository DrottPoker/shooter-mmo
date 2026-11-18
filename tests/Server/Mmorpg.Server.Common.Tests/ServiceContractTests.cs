using Mmorpg.Server.Common.Contracts;

namespace Mmorpg.Server.Common.Tests;

public sealed class ServiceContractTests
{
    [Fact]
    public void TheVersionIsAPositiveNumberThatBothSidesCompile()
    {
        Assert.True(ServiceContract.Version >= 1);
        Assert.Equal("service_contract_mismatch", ServiceContract.MismatchCode);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    public void TheMismatchMessageNamesBothVersionsAndTheGameServiceVersionReadsBack(int? workerVersion, string shown)
    {
        var message = ServiceContract.MismatchMessage(workerVersion);

        Assert.Contains($"service contract version {shown}", message, StringComparison.Ordinal);
        Assert.Contains($"GameService runs version {ServiceContract.Version}", message, StringComparison.Ordinal);
        Assert.Equal(ServiceContract.Version, ServiceContract.TryReadGameServiceVersion(message));
        // The message travels in a realtime protocol field, and in the log of the worker.
        Assert.True(message.Length < 256);
        Assert.All(message, character => Assert.InRange(character, ' ', '~'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Another text.")]
    [InlineData("GameService runs version x")]
    [InlineData("GameService runs version 99999999999999999999")]
    public void ATextThatIsNoMismatchMessageHasNoGameServiceVersion(string? text)
    {
        Assert.Null(ServiceContract.TryReadGameServiceVersion(text));
    }

    [Fact]
    public void AVersionIsReadFromTheMiddleOfAnyText()
    {
        Assert.Equal(12, ServiceContract.TryReadGameServiceVersion("A. GameService runs version 12. B."));
    }
}
