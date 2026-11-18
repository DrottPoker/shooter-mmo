using System.Net;
using System.Text;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.DeploySmokeTest;
using Mmorpg.Tools.SimulationBotClient;

namespace Mmorpg.Tools.Tests;

// The parts of the stack smoke test that need no stack: its options, its report, its walk, its
// answers, and the one place that turns the join answer into a bot identity.
public sealed class DeploySmokeTestTests
{
    private const string Url = "https://localhost:8443";

    [Fact]
    public void TheOptionsDefaultToTheStacksShardWorkerAndTheLengthOfTheWalk()
    {
        var options = SmokeOptions.Parse(["--url", Url, "--ca", "root.crt"]);

        Assert.Equal(new Uri(Url), options.BaseUrl);
        Assert.Equal("root.crt", options.CaPath);
        Assert.Equal("local-shard-1", options.ShardId);
        Assert.Equal("deploy-worker-1", options.WorkerId);
        Assert.Null(options.ExpectedEndpoint);
        Assert.Equal(3, options.Accounts);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Walk);
        Assert.Equal(6, options.RunId.Length);
        Assert.All(options.RunId, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Fact]
    public void TheOptionsTakeEveryValueTheyAreGiven()
    {
        var options = SmokeOptions.Parse([
            "--url", Url, "--ca", "root.crt", "--shard", "local-shard-2", "--worker-id", "w-2",
            "--expected-endpoint", "127.0.0.1:28015", "--accounts", "2", "--walk-seconds", "5",
            "--timeout-seconds", "40", "--run-id", "AB12"]);

        Assert.Equal("local-shard-2", options.ShardId);
        Assert.Equal("w-2", options.WorkerId);
        Assert.Equal("127.0.0.1:28015", options.ExpectedEndpoint);
        Assert.Equal(2, options.Accounts);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Walk);
        Assert.Equal(TimeSpan.FromSeconds(40), options.Timeout);
        Assert.Equal("ab12", options.RunId);
    }

    [Theory]
    [InlineData("--ca", "root.crt")]
    [InlineData("--url", Url)]
    [InlineData("--url", "http://localhost:8080", "--ca", "root.crt")]
    [InlineData("--url", "not a url", "--ca", "root.crt")]
    [InlineData("--url", Url, "--ca", "root.crt", "--accounts", "0")]
    [InlineData("--url", Url, "--ca", "root.crt", "--accounts", "5")]
    [InlineData("--url", Url, "--ca", "root.crt", "--walk-seconds", "soon")]
    [InlineData("--url", Url, "--ca", "root.crt", "--run-id", "has space")]
    [InlineData("--url", Url, "--ca")]
    public void WrongOptionsAreRefusedAndSaySo(params string[] args)
    {
        var exception = Assert.Throws<ArgumentException>(() => SmokeOptions.Parse(args));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void TheReportPassesOnlyWhenEveryCheckHeldAndAtLeastOneRan()
    {
        var output = new StringWriter();
        var empty = new SmokeReport(output);
        var passing = new SmokeReport(output);
        var failing = new SmokeReport(output);

        passing.Pass("one", "held");
        failing.Pass("one", "held");
        failing.Fail("two", "did not hold");

        Assert.Equal(1, empty.ExitCode);
        Assert.Equal(0, passing.ExitCode);
        Assert.Equal(1, failing.ExitCode);
        Assert.Contains("[FAILED] two: did not hold", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("[ok]     one: held", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepThatThrowsFailsOnlyItselfAndTheNextOneStillRuns()
    {
        var report = new SmokeReport(new StringWriter());

        var first = await report.StepAsync("first", () => throw new SmokeException("the first broke"));
        var second = await report.StepAsync("second", () =>
        {
            report.Pass("second", "ran");
            return Task.CompletedTask;
        });

        Assert.False(first);
        Assert.True(second);
        Assert.Equal((1, 1), (report.Passed, report.Failed));
    }

    [Fact]
    public void TheWalkChangesLegsMovesOnAndStaysInsideTheWorld()
    {
        var walker = new WalkingInputSource(1);
        var joined = PlayingTestWorld.Join();
        var settings = joined.MovementSettings;

        var first = walker.SelectPath(10, joined, new SimulationBotPosition(0f, 0f, 0f));
        var sameLeg = walker.SelectPath(11, joined, new SimulationBotPosition(1f, 0f, 0f));
        var nextLeg = walker.SelectPath(10 + (uint)(3 * settings.TickRateHz), joined, new SimulationBotPosition(8f, 0f, 0f));

        Assert.NotNull(first);
        Assert.Same(first, sameLeg);
        Assert.NotNull(nextLeg);
        Assert.NotEqual(first.PathId, nextLeg.PathId);
        Assert.Equal(8d, walker.FarthestMeters, precision: 3);
        // Bot 1 starts north: its first leg ends eight meters from where it stood.
        Assert.Equal((0f, 8f), (first.Waypoints[0].X, first.Waypoints[0].Z));
    }

    [Fact]
    public void TheWalkNeverLeavesTheWorldBounds()
    {
        var walker = new WalkingInputSource(0);
        var joined = PlayingTestWorld.Join();
        var settings = joined.MovementSettings;

        var path = walker.SelectPath(1, joined, new SimulationBotPosition(settings.MaximumX - 2f, 0f, 0f));

        Assert.NotNull(path);
        Assert.Equal(settings.MaximumX - 1f, path.Waypoints[0].X);
    }

    [Fact]
    public void AnswersOfGameServiceAreReadLeniently()
    {
        const string ready = """{"service":"GameService","status":"ready","dependencies":[{"name":"postgres","isReachable":true},{"name":"redis","isReachable":true}]}""";
        const string notReady = """{"status":"not_ready","dependencies":[{"name":"redis","isReachable":false}]}""";

        Assert.Equal("ready", StackJson.Text(ready, "status"));
        Assert.Null(StackJson.Text("<html>no upstreams</html>", "status"));
        Assert.Null(StackJson.Text(string.Empty, "status"));
        Assert.True(StackJson.DependenciesReachable(ready, out var names));
        Assert.Equal("postgres, redis", names);
        Assert.False(StackJson.DependenciesReachable(notReady, out _));
        Assert.False(StackJson.DependenciesReachable("""{"status":"ready"}""", out _));
        Assert.False(StackJson.DependenciesReachable("not json", out _));
    }

    // The join answer as GameService sends it, and what the bot client library gets from it.
    [Fact]
    public void TheJoinAnswerBecomesTheBotIdentityOfTheLibrary()
    {
        var characterId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var json = $$"""
            {"shard":{"id":"local-shard-1","displayName":"Local Shard 1","worldId":"development-world-2","isOnline":true,"activePlayers":0,"capacity":1000},
             "endpoint":{"workerId":"deploy-worker-1","runtimeId":"abc","host":"127.0.0.1","udpPort":28015,"protocolVersion":{{RealtimeProtocol.Version}}},
             "characterId":"{{characterId}}","joinTicket":"ticket-1","expiresAt":"2026-11-18T12:00:00Z","isReconnect":false}
            """;

        var join = System.Text.Json.JsonSerializer.Deserialize<JoinAnswer>(json, StackApi.Json)!;
        var identity = join.ToIdentity(2, accountId, "Smoke One");

        Assert.Equal(("127.0.0.1", 28015), (join.Endpoint.Host, join.Endpoint.UdpPort));
        Assert.Equal(RealtimeProtocol.Version, join.Endpoint.ProtocolVersion);
        Assert.Equal(2, identity.BotIndex);
        Assert.Equal(accountId, identity.AccountId);
        Assert.Equal(characterId, identity.CharacterId);
        Assert.Equal("Smoke One", identity.CharacterName);
        Assert.Equal("local-shard-1", identity.ShardId);
        Assert.Equal("development-world-2", identity.WorldId);
    }

    [Fact]
    public async Task ARegistrationThatMeetsTheRateLimitWaitsOutItsRetryAfterAndGoesAgain()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);
        using var handler = new StubHandler(_ =>
        {
            var status = statuses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                response.Content = new StringContent("""{"code":"rate_limit_exceeded"}""");
            }
            else
            {
                response.Content = new StringContent(
                    """{"accountId":"11111111-1111-1111-1111-111111111111","username":"u","sessionId":"22222222-2222-2222-2222-222222222222","sessionToken":"t","expiresAt":"2026-12-01T00:00:00Z"}""");
            }

            return response;
        });
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(Url) };
        var api = new StackApi(client, rateLimitMargin: TimeSpan.Zero);

        var account = await api.RegisterAsync("a@example.test", "u", "password-123", TestContext.Current.CancellationToken);

        Assert.Equal("u", account.Username);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task ARefusalThatStaysIsReportedWithItsStatusAndCodeButNeverTheRequest()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":"invalid_registration"}""")
        });
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(Url) };
        var api = new StackApi(client);

        var exception = await Assert.ThrowsAsync<SmokeException>(
            () => api.RegisterAsync("a@example.test", "u", "a-password-that-must-not-leak", TestContext.Current.CancellationToken));

        Assert.Equal("register answered 400 invalid_registration.", exception.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
