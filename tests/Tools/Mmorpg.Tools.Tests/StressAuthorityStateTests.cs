using Mmorpg.GameProtocol;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

public sealed class StressAuthorityStateTests
{
    [Fact]
    public void TicketIsExactRuntimeBoundAndOneUse()
    {
        var (state, options, registration) = CreateRegisteredState();
        var issued = state.IssueTicket(7);
        var wrongRuntime = state.ConsumeTicket(new StressConsumeTicketRequest(
            issued.Ticket,
            options.WorkerId,
            "wrong-runtime",
            options.ShardId));

        Assert.False(wrongRuntime.Succeeded);
        Assert.Equal("wrong_simulation_assignment", wrongRuntime.Problem!.Code);

        var consumed = state.ConsumeTicket(new StressConsumeTicketRequest(
            issued.Ticket,
            options.WorkerId,
            registration.RuntimeId,
            options.ShardId));
        Assert.True(consumed.Succeeded);
        Assert.True(consumed.Value!.IsSyntheticBot);
        Assert.Equal(0, consumed.Value.ItemStateRevision);
        Assert.Equal(0, consumed.Value.CarriedWeight);
        Assert.Equal(200, consumed.Value.CarryCapacity);

        var consumedAgain = state.ConsumeTicket(new StressConsumeTicketRequest(
            issued.Ticket,
            options.WorkerId,
            registration.RuntimeId,
            options.ShardId));
        Assert.False(consumedAgain.Succeeded);
        Assert.Equal("invalid_join_ticket", consumedAgain.Problem!.Code);
    }

    [Fact]
    public void SessionHeartbeatAndReleaseRequireExactCredentialsPerSession()
    {
        var (state, options, registration) = CreateRegisteredState();
        var issued = state.IssueTicket(11);
        var consumed = state.ConsumeTicket(new StressConsumeTicketRequest(
            issued.Ticket,
            options.WorkerId,
            registration.RuntimeId,
            options.ShardId));

        Assert.True(consumed.Succeeded);
        var session = consumed.Value!;
        var heartbeat = state.HeartbeatSessions(
            options.WorkerId,
            new StressSessionHeartbeatRequest(
                registration.RuntimeId,
                [
                    new StressSessionHeartbeatEntry(session.SimulationSessionId, "wrong-token"),
                    new StressSessionHeartbeatEntry(session.SimulationSessionId, session.SimulationSessionToken)
                ]));
        Assert.True(heartbeat.Succeeded);
        var wrongHeartbeat = heartbeat.Value!.Sessions[0];
        Assert.Null(wrongHeartbeat.Lease);
        Assert.Equal("simulation_session_changed", wrongHeartbeat.Error!.Code);
        var lease = heartbeat.Value.Sessions[1].Lease;
        Assert.NotNull(lease);
        Assert.False(lease.Released);
        Assert.Equal(0, lease.ItemStateRevision);
        Assert.Equal(0, lease.CarriedWeight);
        Assert.Equal(200, lease.CarryCapacity);
        Assert.Equal((1L, 1L), (state.CaptureCounters().SessionHeartbeats, state.CaptureCounters().SessionHeartbeatRequests));

        var release = state.ReleaseSession(
            session.SimulationSessionId,
            new StressSessionCredentialRequest(
                registration.RuntimeId,
                session.SimulationSessionToken));
        Assert.True(release.Succeeded);
        Assert.True(release.Value!.Released);
        Assert.Equal(0, state.CaptureCounters().ActiveSessions);
    }

    [Fact]
    public void StressIdentityUsesReadableBotNameWithoutDatabaseRecords()
    {
        var (state, _, _) = CreateRegisteredState();

        var issued = state.IssueTicket(42);

        Assert.Equal("Stress Bot 42", issued.CharacterName);
        Assert.NotEqual(Guid.Empty, issued.AccountId);
        Assert.NotEqual(Guid.Empty, issued.CharacterId);
    }

    [Fact]
    public void CapturedWorkerRegistrationUsesLatestHeartbeat()
    {
        var (state, options, registration) = CreateRegisteredState();
        var heartbeat = state.RegisterWorker(
            options.WorkerId,
            new StressWorkerHeartbeatRequest(
                options.FleetId,
                options.NodeId,
                options.ShardId,
                registration.RuntimeId,
                registration.StartedAt,
                options.WorkerHost,
                options.WorkerUdpPort,
                100,
                7,
                RealtimeProtocol.Version,
                "simulation-revision",
                "collision-revision",
                "item-catalog-revision",
                "quest-catalog-revision"));

        Assert.True(heartbeat.Succeeded);
        Assert.Equal(7, state.CaptureWorkerRegistration()!.ActiveConnections);
        Assert.Equal(2, state.CaptureCounters().WorkerHeartbeats);
    }

    private static (
        StressAuthorityState State,
        StressGeneratorOptions Options,
        StressWorkerRegistration Registration) CreateRegisteredState()
    {
        var options = StressGeneratorOptions.Parse(
            ["--worker-secret", "0123456789abcdef0123456789abcdef"],
            Directory.GetCurrentDirectory());
        var state = new StressAuthorityState(options);
        const string runtimeId = "stress-runtime-1";
        var heartbeat = state.RegisterWorker(
            options.WorkerId,
            new StressWorkerHeartbeatRequest(
                options.FleetId,
                options.NodeId,
                options.ShardId,
                runtimeId,
                DateTime.UtcNow,
                options.WorkerHost,
                options.WorkerUdpPort,
                100,
                0,
                RealtimeProtocol.Version,
                "simulation-revision",
                "collision-revision",
                "item-catalog-revision",
                "quest-catalog-revision"));

        Assert.True(heartbeat.Succeeded);
        var value = heartbeat.Value!;
        return (
            state,
            options,
            new StressWorkerRegistration(
                value.WorkerId,
                value.RuntimeId,
                value.FleetId,
                value.NodeId,
                value.ShardId,
                value.WorldId,
                value.Host,
                value.UdpPort,
                value.MaxConnections,
                value.ActiveConnections,
                value.ProtocolVersion,
                value.SimulationRevision,
                value.CollisionRevision,
                DateTime.UtcNow,
                value.LastHeartbeatAt,
                value.OnlineUntil,
                value.ItemCatalogRevision,
                value.QuestCatalogRevision));
    }
}
