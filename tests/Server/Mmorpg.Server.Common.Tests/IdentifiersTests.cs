using Mmorpg.Server.Common.Identity;

namespace Mmorpg.Server.Common.Tests;

public sealed class IdentifiersTests
{
    [Fact]
    public void ContentIdentifiersAllowDotsAndServiceIdentifiersDoNot()
    {
        Assert.True(Identifiers.IsContentIdentifier("mob.feral_wolf-2"));
        Assert.False(Identifiers.IsServiceIdentifier("mob.feral_wolf-2"));
        Assert.True(Identifiers.IsServiceIdentifier("local-simulation-worker_1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("mob wolf")]
    [InlineData("mob\u00e9")]
    public void InvalidIdentifiersAreRejectedByBothShapes(string? value)
    {
        Assert.False(Identifiers.IsContentIdentifier(value));
        Assert.False(Identifiers.IsServiceIdentifier(value));
    }

    [Fact]
    public void IdentifiersAreLimitedTo128Characters()
    {
        Assert.True(Identifiers.IsServiceIdentifier(new string('a', 128)));
        Assert.False(Identifiers.IsServiceIdentifier(new string('a', 129)));
        Assert.False(Identifiers.IsContentIdentifier(new string('a', 129)));
    }
}
