using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.Testing;

namespace Mmorpg.SimulationWorker.Tests;

// The checked-in local topology runs two shards side by side: local-shard-1 on the test
// World with the default worker profile, and local-shard-2, the stress arena, with the
// StressArena profile. The workers, GameService, and the stress tools must agree on it.
public sealed class LocalShardTopologyTests
{
    [Fact]
    public void StressArenaProfileRunsDevelopmentWorldOneBesideTheDefaultWorker()
    {
        var defaultWorker = WorkerConfig("SimulationWorker");
        var stressWorker = WorkerConfig("StressArena");

        Assert.Equal("local-shard-1", defaultWorker.ShardId);
        Assert.Equal("development-world-2", defaultWorker.WorldId);
        Assert.Equal("local-shard-2", stressWorker.ShardId);
        Assert.Equal("development-world-1", stressWorker.WorldId);
        Assert.NotEqual(defaultWorker.SimulationWorkerId, stressWorker.SimulationWorkerId);
        Assert.NotEqual(defaultWorker.UdpPort, stressWorker.UdpPort);
        Assert.Equal(stressWorker.UdpPort, stressWorker.AdvertisedUdpPort);

        var shards = GameServiceShardIds();
        Assert.Contains(defaultWorker.ShardId, shards);
        Assert.Contains(stressWorker.ShardId, shards);
        Assert.Equal(
            stressWorker.ShardId,
            ReadJson("tools", "ActiveSimulationBots", "appsettings.json")
                .GetProperty("ActiveSimulationBots")
                .GetProperty("ShardId")
                .GetString());
    }

    private static SimulationWorkerConfig WorkerConfig(string profile)
    {
        var environment = ReadJson("src", "Server", "SimulationWorker", "Properties", "launchSettings.json")
            .GetProperty("profiles")
            .GetProperty(profile)
            .GetProperty("environmentVariables")
            .EnumerateObject()
            .ToDictionary(variable => variable.Name, variable => variable.Value.GetString());
        environment["SimulationWorker:WorldDataPath"] = RepositoryPaths.Worlds;
        environment["SIMULATION_WORKER_SERVICE_SECRET"] = "local-topology-test-secret-that-is-long";
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepositoryFile("src", "Server", "SimulationWorker", "Config", "appsettings.json"))
            .AddInMemoryCollection(environment)
            .Build();
        return SimulationWorkerConfig.FromConfiguration(configuration);
    }

    private static string[] GameServiceShardIds()
    {
        return ReadJson("src", "Server", "GameService", "Config", "appsettings.json")
            .GetProperty("Simulation")
            .GetProperty("Topology")
            .GetProperty("Shards")
            .EnumerateArray()
            .Select(shard => shard.GetProperty("Id").GetString()!)
            .ToArray();
    }

    private static JsonElement ReadJson(params string[] path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(path)));
        return document.RootElement.Clone();
    }

    private static string RepositoryFile(params string[] path)
    {
        return Path.Combine([RepositoryPaths.Root, .. path]);
    }
}
