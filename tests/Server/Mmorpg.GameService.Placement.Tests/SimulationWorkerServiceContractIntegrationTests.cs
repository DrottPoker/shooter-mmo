using Mmorpg.GameService.Testing;
using Mmorpg.Server.Common.Contracts;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// The call between a worker and GameService has a version. GameService refuses the registration
// heartbeat of another version, or of none, with a code of its own and a message that names both
// versions, so a worker and a GameService of different versions never register or play together.
public sealed class SimulationWorkerServiceContractIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";

    [PostgresIntegrationFact]
    public async Task AHeartbeatOfTheCurrentVersionRegistersAndReportsTheSessionLease()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);

        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        // Every answer, a registration and a renewal alike, tells the worker how long a session
        // lease is, so it can check it against its own timing.
        Assert.Equal(
            int.Parse(context.Configuration["Simulation:SessionLeaseLifetimeSeconds"]!),
            heartbeat.Value!.SessionLeaseSeconds);
        var renewal = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(),
            CancellationToken.None);
        Assert.True(renewal.Succeeded, renewal.Error?.Message);
        Assert.Equal(heartbeat.Value.SessionLeaseSeconds, renewal.Value!.SessionLeaseSeconds);
    }

    [PostgresIntegrationFact]
    public async Task AHeartbeatOfAnotherVersionOrOfNoneIsRefusedAndRegistersNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var workersBefore = await context.ExecuteScalarIntAsync("select count(*) from simulation_workers where runtime_id is not null;");
        var assignmentsBefore = await context.ExecuteScalarIntAsync("select count(*) from simulation_assignments;");

        foreach (int? workerVersion in new int?[] { null, 0, ServiceContract.Version + 1 })
        {
            var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
                WorkerId,
                PostgresIntegrationTestContext.CreateHeartbeatRequest() with { ServiceContractVersion = workerVersion },
                CancellationToken.None);

            Assert.False(heartbeat.Succeeded);
            Assert.Equal(409, heartbeat.StatusCode);
            Assert.Equal(ServiceContract.MismatchCode, heartbeat.Error!.Code);
            var worker = workerVersion?.ToString() ?? "none";
            Assert.Contains($"version {worker}", heartbeat.Error.Message, StringComparison.Ordinal);
            Assert.Contains($"version {ServiceContract.Version}", heartbeat.Error.Message, StringComparison.Ordinal);
            Assert.Equal(ServiceContract.Version, ServiceContract.TryReadGameServiceVersion(heartbeat.Error.Message));
        }

        Assert.Equal(workersBefore, await context.ExecuteScalarIntAsync("select count(*) from simulation_workers where runtime_id is not null;"));
        Assert.Equal(assignmentsBefore, await context.ExecuteScalarIntAsync("select count(*) from simulation_assignments;"));
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync("select count(*) from simulation_workers where runtime_id = 'integration-worker-runtime';"));
    }

    // The refusal comes before every other check, since a worker of another version may send a
    // request of another shape, and it applies to a renewal as well as to a registration.
    [PostgresIntegrationFact]
    public async Task ARegisteredWorkerThatSendsAnotherVersionIsRefusedToo()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();

        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            WorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest() with
            {
                ServiceContractVersion = ServiceContract.Version + 1,
                FleetId = "not a valid identifier"
            },
            CancellationToken.None);

        Assert.Equal(ServiceContract.MismatchCode, heartbeat.Error!.Code);
    }
}
