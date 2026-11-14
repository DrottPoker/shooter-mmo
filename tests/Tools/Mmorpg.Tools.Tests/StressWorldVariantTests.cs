using System.Security.Cryptography;
using Mmorpg.Testing;
using Mmorpg.Tools.StackStressGenerator;
using Mmorpg.Tools.WorldActorCompiler;

namespace Mmorpg.Tools.Tests;

// The load variant of a World, written into a run folder with the real content compilers.
public sealed class StressWorldVariantTests
{
    [Fact]
    public void TheVariantAddsTheScenarioAreasAndLeavesTheRepositoryUnchanged()
    {
        var before = RepositoryContentHash();
        var scenario = StressLoadScenario.Load(Path.Combine(RepositoryPaths.Root, "tools", "load", "scenarios", "playing-smoke.json"));
        var folder = TemporaryFolder();
        try
        {
            var result = StressWorldVariant.Write(scenario, Path.Combine(folder, "world-variant"), RepositoryPaths.Root, TextWriter.Null);

            Assert.Equal(28, result.AddedInstances);
            // The node field's veins block movement, so the collision changes.
            Assert.False(result.KeepsCollisionRevision);
            var spawns = WorldActorJson.DeserializeSpawnRuntime(File.ReadAllText(Path.Combine(
                result.WorkerWorldDataPath,
                "development-world-2",
                "world-actor-spawns.json")));
            Assert.Equal(
                10,
                spawns.SpawnInstances.Count(instance =>
                    instance.SpawnDefinitionId == "development_world_2.load_west_field_wolves"
                    && instance.ActorId == "mob.feral_wolf"));
            Assert.Equal(
                10,
                spawns.SpawnInstances.Count(instance =>
                    instance.SpawnDefinitionId == "development_world_2.load_south_field_wolves"
                    && instance.ActorId == "mob.grey_wolf"));
            Assert.Equal(
                8,
                spawns.SpawnInstances.Count(instance =>
                    instance.SpawnDefinitionId.StartsWith("development_world_2.load_meadow_veins.node", StringComparison.Ordinal)
                    && instance.ActorId == "resource.iron_vein"));
            Assert.True(File.Exists(Path.Combine(result.WorkerWorldDataPath, "development-world-2", "Collision", "manifest.json")));
            Assert.True(File.Exists(Path.Combine(result.WorkerWorldDataPath, "development-world-2", "world.json")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal(before, RepositoryContentHash());
    }

    // Resource nodes block movement with their collision boxes, so a variant with a node group
    // changes the collision revision and a Unity client cannot join it.
    [Fact]
    public void ANodeGroupChangesTheCollisionRevision()
    {
        var scenario = VariantScenario(
            "\"groups\": [ { \"name\": \"south-veins\", \"actorId\": \"resource.iron_vein\", \"centerX\": 60, \"centerZ\": -150, \"rows\": 2, \"columns\": 2, \"spacing\": 6 } ]");
        var folder = TemporaryFolder();
        try
        {
            var result = StressWorldVariant.Write(scenario, Path.Combine(folder, "world-variant"), RepositoryPaths.Root, TextWriter.Null);

            Assert.Equal(4, result.AddedInstances);
            Assert.False(result.KeepsCollisionRevision);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The worker stands a resource node at the height of its spawn and bakes its collision
    // there, so on a slope every node of a group gets the height of the ground under it.
    [Fact]
    public void TheNodesOfAGroupOnASlopeStandOnTheGround()
    {
        var scenario = VariantScenario(
            "\"groups\": [ { \"name\": \"hill-veins\", \"actorId\": \"resource.iron_vein\", \"centerX\": 132, \"centerZ\": 152, \"rows\": 2, \"columns\": 2, \"spacing\": 6 } ]");
        var folder = TemporaryFolder();
        try
        {
            var result = StressWorldVariant.Write(scenario, Path.Combine(folder, "world-variant"), RepositoryPaths.Root, TextWriter.Null);

            Assert.Equal(4, result.AddedInstances);
            var spawns = WorldActorJson.DeserializeSpawnRuntime(File.ReadAllText(Path.Combine(
                result.WorkerWorldDataPath,
                "development-world-2",
                "world-actor-spawns.json")));
            var heights = Enumerable.Range(0, 4)
                .Select(index => spawns.SpawnInstances.Single(instance =>
                    instance.SpawnDefinitionId == StressWorldVariant.VariantNodeId("development-world-2", "hill-veins", index)).Y)
                .ToArray();
            // The ground is well above the World's base height here, and not level.
            Assert.All(heights, height => Assert.True(height > 1f, $"height {height}"));
            Assert.True(heights.Max() - heights.Min() > 0.1f, string.Join(", ", heights));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"name\": \"out-of-world\", \"actorId\": \"mob.feral_wolf\", \"minimumX\": 300, \"minimumZ\": 300, \"maximumX\": 320, \"maximumZ\": 320, \"count\": 3", "'out-of-world'")]
    [InlineData("\"name\": \"at-the-spawn\", \"actorId\": \"mob.feral_wolf\", \"minimumX\": -4, \"minimumZ\": -16, \"maximumX\": 4, \"maximumZ\": -8, \"count\": 3", "closer than 12 m to the World spawn")]
    public void AnAreaWithoutWalkableGroundOrAtTheSpawnFailsByName(string area, string expected)
    {
        var scenario = VariantScenario("\"areas\": [ { " + area + " } ]");
        var folder = TemporaryFolder();
        try
        {
            var exception = Assert.Throws<StressGeneratorOptionException>(() =>
                StressWorldVariant.Write(scenario, Path.Combine(folder, "world-variant"), RepositoryPaths.Root, TextWriter.Null));

            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static StressLoadScenario VariantScenario(string variant)
    {
        return StressLoadScenario.Parse(
            "variant",
            $$"""
            {
              "description": "test",
              "mode": "full-stack",
              "workload": "inventory",
              "shardId": "local-shard-2",
              "worldId": "development-world-2",
              "generator": { "bots": 1, "duration-seconds": 10 },
              "worldVariant": { {{variant}} }
            }
            """,
            "variant.json");
    }

    private static string TemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "world-variant-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // Every file of content/ and the checked-in generated data, by path and content.
    private static string RepositoryContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in new[] { RepositoryPaths.Content, RepositoryPaths.Generated })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(RepositoryPaths.Root, file)));
                hash.AppendData(File.ReadAllBytes(file));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
