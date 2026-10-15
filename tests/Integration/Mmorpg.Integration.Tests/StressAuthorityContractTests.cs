using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Integration.Tests;

// Worker-only stress runs replace GameService with the tool's in-memory authority. Its
// responses must keep passing the worker's own GameService validation, or every bot is
// rejected with invalid_auth_response.
public sealed class StressAuthorityContractTests
{
    private const string WorkerSecret = "stress-contract-test-worker-secret-2026";

    [Fact]
    public async Task WorkerAcceptsTheStressAuthorityRegistrationJoinHeartbeatAndRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = StressGeneratorOptions.Parse(
            ["--worker-secret", WorkerSecret],
            Directory.GetCurrentDirectory());
        var authority = new StressAuthorityState(options);
        await using var app = StressAuthorityHost.Create(authority, "http://127.0.0.1:0");
        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var httpClient = new HttpClient { BaseAddress = new Uri(address) };
        httpClient.DefaultRequestHeaders.Add(
            GameServiceAuthenticationHandler.SimulationWorkerIdHeader,
            options.WorkerId);
        httpClient.DefaultRequestHeaders.Add(
            GameServiceAuthenticationHandler.SimulationWorkerSecretHeader,
            WorkerSecret);
        var client = new GameServiceClient(httpClient);
        const string runtimeId = "stress-contract-runtime";

        var registration = await client.HeartbeatSimulationWorkerAsync(
            options.WorkerId,
            new SimulationWorkerHeartbeatRequest(
                options.FleetId,
                options.NodeId,
                options.ShardId,
                options.WorldId,
                runtimeId,
                DateTime.UtcNow,
                options.WorkerHost,
                options.WorkerUdpPort,
                100,
                0,
                RealtimeProtocol.Version,
                "simulation-revision",
                "collision-revision"),
            cancellationToken);
        Assert.True(registration.Succeeded, registration.Error?.Code);

        var ticket = authority.IssueTicket(1);
        var joined = await client.ConsumeJoinTicketAsync(
            ticket.Ticket,
            options.WorkerId,
            runtimeId,
            options.ShardId,
            cancellationToken);
        Assert.True(joined.Succeeded, joined.Error?.Code);
        var session = joined.Value!;
        Assert.True(session.SessionLeaseDurationMilliseconds > 0);

        var heartbeat = await client.HeartbeatSimulationSessionAsync(
            session.SimulationSessionId,
            runtimeId,
            session.SimulationSessionToken,
            cancellationToken);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Code);
        Assert.True(heartbeat.Value!.LeaseDurationMilliseconds > 0);

        var release = await client.ReleaseSimulationSessionAsync(
            session.SimulationSessionId,
            runtimeId,
            session.SimulationSessionToken,
            cancellationToken);
        Assert.True(release.Succeeded, release.Error?.Code);
        Assert.True(release.Value!.Released);
        Assert.Equal(0, release.Value.LeaseDurationMilliseconds);

        await app.StopAsync(cancellationToken);
    }
}
