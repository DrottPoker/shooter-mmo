namespace Mmorpg.Integration.Tests;

public sealed class TestBuildOutputTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("Config/appsettings.json")]
    [InlineData("Config/appsettings.Development.json")]
    public void TestOutputOwnsEmptySettingsInsteadOfAnyServiceCopy(string relativePath)
    {
        // Several referenced services ship settings at these paths. The test output keeps
        // its own empty placeholder, so no test can silently read one service's file.
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);

        Assert.Equal("{}", File.ReadAllText(path).Trim());
    }
}
