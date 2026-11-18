using Microsoft.AspNetCore.Http;

namespace Mmorpg.GameService.Placement.Tests;

// The version question of a join: a client plays only on a worker that runs exactly its protocol
// version and simulation revision, and its World's collision revision when it names one.
public sealed class JoinVersionCheckTests
{
    private const int Protocol = 46;
    private const string Simulation = "movement-simulation-v7";
    private const string World = "development-world-2";
    private const string Collision = "abcdef0123456789";

    private static readonly WorkerVersions Worker = new(Protocol, Simulation, World, Collision);

    [Fact]
    public void AClientWithTheWorkersVersionsFits()
    {
        Assert.Null(JoinVersionCheck.Refusal(Protocol, Simulation, World, Collision, Worker));
        Assert.Null(JoinVersionCheck.Refusal(Protocol, Simulation, null, null, Worker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AClientWithoutAProtocolVersionMustUpdate(int? protocolVersion)
    {
        Assert.Equal(
            JoinVersionCheck.IncompatibleProtocol,
            JoinVersionCheck.Refusal(protocolVersion, Simulation, World, Collision, Worker));
    }

    [Fact]
    public void AWorkerAheadOfTheClientMeansTheClientMustUpdate()
    {
        Assert.Equal(
            JoinVersionCheck.IncompatibleProtocol,
            JoinVersionCheck.Refusal(Protocol - 1, Simulation, World, Collision, Worker));
    }

    [Fact]
    public void AWorkerBehindTheClientMeansADeploymentIsStillReplacingIt()
    {
        Assert.Equal(
            JoinVersionCheck.ShardNotUpdated,
            JoinVersionCheck.Refusal(Protocol + 1, Simulation, World, Collision, Worker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("movement-simulation-v6")]
    [InlineData("MOVEMENT-SIMULATION-V7")]
    public void AnotherSimulationRevisionAtTheSameProtocolHasNoOrder(string? simulationRevision)
    {
        Assert.Equal(
            JoinVersionCheck.IncompatibleSimulation,
            JoinVersionCheck.Refusal(Protocol, simulationRevision, World, Collision, Worker));
    }

    [Fact]
    public void TheProtocolIsJudgedBeforeTheSimulationRevision()
    {
        Assert.Equal(
            JoinVersionCheck.IncompatibleProtocol,
            JoinVersionCheck.Refusal(Protocol - 1, "another-simulation", World, Collision, Worker));
    }

    [Fact]
    public void OtherCollisionDataForTheShardsWorldIsRefusedAndTheHexCaseDoesNotMatter()
    {
        Assert.Equal(
            JoinVersionCheck.IncompatibleWorldData,
            JoinVersionCheck.Refusal(Protocol, Simulation, World, "0000000000000000", Worker));
        Assert.Null(JoinVersionCheck.Refusal(Protocol, Simulation, World, Collision.ToUpperInvariant(), Worker));
    }

    // A client with a stale Shard list names the World it believes the Shard runs; its data for
    // another World says nothing about the Shard's, so it is never told that it is out of date.
    [Theory]
    [InlineData("development-world-1", "0000000000000000")]
    [InlineData(null, "0000000000000000")]
    [InlineData("development-world-2", null)]
    [InlineData("development-world-2", " ")]
    public void CollisionDataIsJudgedOnlyWhenTheClientNamesTheShardsWorldAndItsRevision(
        string? worldId,
        string? collisionRevision)
    {
        Assert.Null(JoinVersionCheck.Refusal(Protocol, Simulation, worldId, collisionRevision, Worker));
    }

    [Fact]
    public void TheRefusalIsA409WithItsCode()
    {
        var result = JoinVersionCheck.Conflict<string>(JoinVersionCheck.ShardNotUpdated, Worker);

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("shard_not_updated", result.Error!.Code);
    }
}
