using AuthService.Config;
using AuthService.Simulation;
using Microsoft.Extensions.Configuration;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class DevelopmentSimulationBotAuthorityTests
{
    [Fact]
    public void ConfigurationRejectsEnabledBotsOutsideDevelopment()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["DevelopmentSimulationBots:Enabled"] = "true",
                ["ACTIVE_SIMULATION_BOTS_SECRET"] = new string('s', 32)
            });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentSimulationBotOptions.FromConfiguration(
                configuration,
                isDevelopment: false));

        Assert.Contains("only be true in the Development environment", exception.Message);
    }

    [Fact]
    public void ConfigurationRejectsShortAuthoritySecretWhenEnabled()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["DevelopmentSimulationBots:Enabled"] = "true",
                ["ACTIVE_SIMULATION_BOTS_SECRET"] = "too-short"
            });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentSimulationBotOptions.FromConfiguration(
                configuration,
                isDevelopment: true));

        Assert.Contains("non-placeholder secret of at least 32 characters", exception.Message);
    }

    [Fact]
    public void TicketIsOneUseAndSessionSupportsHeartbeatAndRelease()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(
            2026,
            7,
            14,
            10,
            0,
            0,
            TimeSpan.Zero));
        var authority = new DevelopmentSimulationBotAuthority(
            CreateOptions(),
            timeProvider);
        var botInstanceId = Guid.NewGuid();
        var issued = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(
                botInstanceId,
                7,
                "local-shard-1"),
            CreatePlacement());

        Assert.True(issued.Succeeded);
        Assert.Equal("Active Bot 7", issued.Value!.CharacterName);

        var consumeRequest = new ConsumeSimulationJoinTicketRequest(
            issued.Value.JoinTicket,
            "worker-1",
            "runtime-1",
            "local-shard-1");
        Assert.True(authority.TryConsumeTicket(consumeRequest, out var consumed));
        Assert.True(consumed.Succeeded);
        Assert.False(consumed.Value!.IsReconnect);
        Assert.True(consumed.Value.IsSyntheticBot);

        Assert.True(authority.TryConsumeTicket(consumeRequest, out var consumedAgain));
        Assert.False(consumedAgain.Succeeded);
        Assert.Equal("invalid_join_ticket", consumedAgain.Error!.Code);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var credentials = new SimulationSessionCredentialRequest(
            "runtime-1",
            consumed.Value.SimulationSessionToken);
        Assert.True(authority.TryHeartbeatSession(
            consumed.Value.SimulationSessionId,
            "worker-1",
            credentials,
            out var heartbeat));
        Assert.True(heartbeat.Succeeded);
        Assert.False(heartbeat.Value!.Released);

        Assert.True(authority.TryReleaseSession(
            consumed.Value.SimulationSessionId,
            "worker-1",
            credentials,
            out var release));
        Assert.True(release.Succeeded);
        Assert.True(release.Value!.Released);

        var snapshot = authority.CaptureSnapshot();
        Assert.Equal(1, snapshot.Identities);
        Assert.Equal(0, snapshot.PendingTickets);
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(1, snapshot.ReleasedOrExpiredSessions);

        var reissued = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(
                botInstanceId,
                7,
                "local-shard-1"),
            CreatePlacement());
        Assert.True(reissued.Succeeded);
        Assert.Equal(issued.Value.AccountId, reissued.Value!.AccountId);
        Assert.Equal(issued.Value.CharacterId, reissued.Value.CharacterId);
        Assert.Equal(issued.Value.CharacterName, reissued.Value.CharacterName);
    }

    [Fact]
    public void WrongWorkerDoesNotConsumeTicket()
    {
        var authority = new DevelopmentSimulationBotAuthority(
            CreateOptions(),
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var issued = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                1,
                "local-shard-1"),
            CreatePlacement());

        Assert.True(authority.TryConsumeTicket(
            new ConsumeSimulationJoinTicketRequest(
                issued.Value!.JoinTicket,
                "other-worker",
                "runtime-1",
                "local-shard-1"),
            out var wrongWorker));
        Assert.Equal("wrong_simulation_assignment", wrongWorker.Error!.Code);

        Assert.True(authority.TryConsumeTicket(
            new ConsumeSimulationJoinTicketRequest(
                issued.Value.JoinTicket,
                "worker-1",
                "runtime-1",
                "local-shard-1"),
            out var consumed));
        Assert.True(consumed.Succeeded);
    }

    [Fact]
    public void WorkerConnectionCapacityRejectsAdditionalBotTicket()
    {
        var authority = new DevelopmentSimulationBotAuthority(
            CreateOptions(),
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var placement = CreatePlacement() with
        {
            MaxConnections = 10,
            ReportedActiveConnections = 10
        };

        var result = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                1,
                "local-shard-1"),
            placement);

        Assert.False(result.Succeeded);
        Assert.Equal("development_bot_worker_capacity_reached", result.Error!.Code);
    }

    [Fact]
    public void MoreThanTwoHundredDistinctBotIdentitiesCanReceiveTickets()
    {
        var authority = new DevelopmentSimulationBotAuthority(
            CreateOptions(),
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var placement = CreatePlacement() with { MaxConnections = 1_000 };

        for (var index = 1; index <= 500; index++)
        {
            var issued = authority.IssueTicket(
                new DevelopmentSimulationBotTicketRequest(
                    Guid.NewGuid(),
                    index,
                    "local-shard-1"),
                placement);

            Assert.True(issued.Succeeded, issued.Error?.Message);
        }

        var snapshot = authority.CaptureSnapshot();
        Assert.Equal(500, snapshot.Identities);
        Assert.Equal(500, snapshot.PendingTickets);
    }

    [Fact]
    public void ActiveInMemorySessionsCountBeforeNextWorkerHeartbeat()
    {
        var authority = new DevelopmentSimulationBotAuthority(
            CreateOptions(),
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var placement = CreatePlacement() with { MaxConnections = 4 };

        for (var index = 1; index <= 4; index++)
        {
            var issued = authority.IssueTicket(
                new DevelopmentSimulationBotTicketRequest(
                    Guid.NewGuid(),
                    index,
                    "local-shard-1"),
                placement);
            Assert.True(issued.Succeeded);
            Assert.True(authority.TryConsumeTicket(
                new ConsumeSimulationJoinTicketRequest(
                    issued.Value!.JoinTicket,
                    "worker-1",
                    "runtime-1",
                    "local-shard-1"),
                out var consumed));
            Assert.True(consumed.Succeeded);
        }

        var rejected = authority.IssueTicket(
            new DevelopmentSimulationBotTicketRequest(
                Guid.NewGuid(),
                5,
                "local-shard-1"),
            placement);

        Assert.False(rejected.Succeeded);
        Assert.Equal("development_bot_worker_capacity_reached", rejected.Error!.Code);
    }

    private static DevelopmentSimulationBotOptions CreateOptions()
    {
        return new DevelopmentSimulationBotOptions(
            true,
            new string('s', 32),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            "Active Bot");
    }

    private static DevelopmentSimulationBotPlacement CreatePlacement()
    {
        return new DevelopmentSimulationBotPlacement(
            "local-shard-1",
            "local-world-1",
            "worker-1",
            "runtime-1",
            "127.0.0.1",
            27015,
            100,
            0,
            0,
            0,
            1,
            "simulation-revision",
            "collision-revision");
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return current;
        }

        public void Advance(TimeSpan duration)
        {
            current = current.Add(duration);
        }
    }
}
