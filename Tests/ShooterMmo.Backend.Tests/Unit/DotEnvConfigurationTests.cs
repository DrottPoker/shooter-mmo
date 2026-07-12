using Microsoft.Extensions.Configuration;
using ShooterMmo.Shared.Configuration;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class DotEnvConfigurationTests
{
    [Fact]
    public void LoadsValuesAndMapsDoubleUnderscoresToConfigurationSections()
    {
        var directory = Directory.CreateTempSubdirectory("shooter-mmo-dotenv-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, ".env"),
                "ConnectionStrings__Redis=localhost:6379\nQuotedValue=\"hello world\"\n");

            var configuration = new ConfigurationBuilder()
                .AddOptionalDotEnvFile(directory.FullName)
                .Build();

            Assert.Equal("localhost:6379", configuration.GetConnectionString("Redis"));
            Assert.Equal("hello world", configuration["QuotedValue"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
