using Microsoft.Extensions.Configuration;
using ShooterMmo.Tools.ActiveSimulationBots;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ActiveSimulationBotOptionsTests
{
    [Theory]
    [InlineData(5, 5, 3, 0)]
    [InlineData(8, 5, 10, 3)]
    [InlineData(10, 5, 2, 2)]
    public void IntentionalLeavesNeverCrossMinimum(
        int joined,
        int minimum,
        int requested,
        int expected)
    {
        Assert.Equal(
            expected,
            ActiveSimulationBotPopulationPolicy.GetAllowedIntentionalLeaves(
                joined,
                minimum,
                requested));
    }

    [Theory]
    [InlineData(4, 5, 5, 10, true)]
    [InlineData(5, 5, 5, 10, false)]
    [InlineData(4, 5, 10, 10, false)]
    public void ConnectionStartRespectsTargetAndMaximumOccupancy(
        int desired,
        int target,
        int occupancy,
        int maximum,
        bool expected)
    {
        Assert.Equal(
            expected,
            ActiveSimulationBotPopulationPolicy.CanStartConnection(
                desired,
                target,
                occupancy,
                maximum));
    }

    [Fact]
    public void ParsesValidPopulationAndBehaviorConfiguration()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ActiveSimulationBots:AuthorityUrl"] = "http://127.0.0.1:5000",
                ["ACTIVE_SIMULATION_BOTS_SECRET"] = new string('s', 32),
                ["ActiveSimulationBots:ShardId"] = "local-shard-1",
                ["ActiveSimulationBots:MinimumActiveBots"] = "3",
                ["ActiveSimulationBots:MaximumActiveBots"] = "9",
                ["ActiveSimulationBots:StartupBotsPerSecond"] = "2.5"
            });

        var options = ActiveSimulationBotOptions.FromConfiguration(configuration);

        Assert.Equal(3, options.MinimumActiveBots);
        Assert.Equal(9, options.MaximumActiveBots);
        Assert.Equal(2.5d, options.StartupBotsPerSecond);
        Assert.Equal("local-shard-1", options.ShardId);
    }

    [Fact]
    public void RejectsNonLoopbackAuthority()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ActiveSimulationBots:AuthorityUrl"] = "http://example.com",
                ["ACTIVE_SIMULATION_BOTS_SECRET"] = new string('s', 32),
                ["ActiveSimulationBots:ShardId"] = "local-shard-1",
                ["ActiveSimulationBots:MinimumActiveBots"] = "1",
                ["ActiveSimulationBots:MaximumActiveBots"] = "2"
            });

        var exception = Assert.Throws<ActiveSimulationBotConfigurationException>(() =>
            ActiveSimulationBotOptions.FromConfiguration(configuration));

        Assert.Contains("loopback HTTP URL", exception.Message);
    }

    [Fact]
    public void RejectsInvertedPopulationRange()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ActiveSimulationBots:AuthorityUrl"] = "http://127.0.0.1:5000",
                ["ACTIVE_SIMULATION_BOTS_SECRET"] = new string('s', 32),
                ["ActiveSimulationBots:ShardId"] = "local-shard-1",
                ["ActiveSimulationBots:MinimumActiveBots"] = "10",
                ["ActiveSimulationBots:MaximumActiveBots"] = "2"
            });

        var exception = Assert.Throws<ActiveSimulationBotConfigurationException>(() =>
            ActiveSimulationBotOptions.FromConfiguration(configuration));

        Assert.Contains("MinimumActiveBots cannot exceed MaximumActiveBots", exception.Message);
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
