using Microsoft.Extensions.Configuration;
using Mmorpg.Server.Common.Configuration;

namespace Mmorpg.Backend.Tests.Unit;

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

    [Fact]
    public void ExportPrefixesAndInlineCommentsAreHandledAndSecretsNeverLeak()
    {
        var values = DotEnvConfigurationExtensions.Parse(
            ".env",
            [
                "export SIMULATION_WORKER_SERVICE_SECRET=abc123 # local only",
                "QuotedHash=\"value # kept\""
            ]);

        Assert.Equal("abc123", values["SIMULATION_WORKER_SERVICE_SECRET"]);
        Assert.Equal("value # kept", values["QuotedHash"]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DotEnvConfigurationExtensions.Parse(".env", ["# comment", "super-secret-value"]));
        Assert.Contains("line 2", exception.Message);
        Assert.DoesNotContain("super-secret-value", exception.Message);
    }

    [Fact]
    public void ServiceOutputFolderFindsTheRepositoryRootEnvFile()
    {
        var repository = Directory.CreateTempSubdirectory("shooter-mmo-repo-");

        try
        {
            File.WriteAllText(Path.Combine(repository.FullName, "Mmorpg.slnx"), string.Empty);
            File.WriteAllText(Path.Combine(repository.FullName, ".env"), "RepositoryValue=root\n");
            var outputFolder = Directory.CreateDirectory(Path.Combine(
                repository.FullName,
                "GameService",
                "bin",
                "Debug",
                "net10.0"));

            var configuration = new ConfigurationBuilder()
                .AddOptionalDotEnvFile(outputFolder.FullName)
                .Build();

            Assert.Equal("root", configuration["RepositoryValue"]);
        }
        finally
        {
            repository.Delete(recursive: true);
        }
    }
}
