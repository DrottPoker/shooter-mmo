using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Accounts.Redis;
using Mmorpg.Testing;

namespace Mmorpg.Integration.Tests;

// Settings of the two processes that only work together, checked against the files that ship.
public sealed class ServiceTimingContractTests
{
    // A login or logout that waits for Redis holds a pooled connection and the row locks of the account
    // and its sessions for as long as its revocation deadline, and a worker call that waits for one of
    // them times out with it. So the deadline must stay well below the call timeout that the worker
    // gives every call, at most half of it.
    [Fact]
    public void TheRevocationDeadlineIsWellBelowTheWorkersCallTimeout()
    {
        using var worker = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src",
            "Server",
            "SimulationWorker",
            "Config",
            "appsettings.json")));
        var callTimeout = TimeSpan.FromSeconds(
            worker.RootElement.GetProperty("SimulationWorker").GetProperty("GameServiceTimeoutSeconds").GetInt32());

        var deadline = RedisAccelerationOptions
            .FromConfiguration(new ConfigurationBuilder().Build())
            .SessionRevocationDeadline;

        Assert.True(
            deadline * 2 <= callTimeout,
            $"The revocation deadline of {deadline} is not well below the worker's call timeout of {callTimeout}.");
    }
}
