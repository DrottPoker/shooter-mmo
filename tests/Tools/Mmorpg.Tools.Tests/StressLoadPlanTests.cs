using Mmorpg.Testing;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// The scenarios of tools/load/scenarios and the plan that tools/Test-Load.ps1 runs them with.
public sealed class StressLoadPlanTests
{
    private static string LoadDirectory => Path.Combine(RepositoryPaths.Root, "tools", "load");

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(LoadDirectory, "scenarios"), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    [Fact]
    public void TheShippedScenariosAreThere()
    {
        var names = Scenarios().Select(row => row.Data).ToArray();

        Assert.Subset(
            new HashSet<string>(names),
            new HashSet<string> { "smoke", "worker-only-200", "worker-only-500", "mixed-100", "mixed-250", "session-cache-on", "session-cache-off" });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryShippedScenarioLoadsAndPlans(string name)
    {
        var scenario = StressLoadScenario.Load(Path.Combine(LoadDirectory, "scenarios", name + ".json"));

        var plan = Plan(scenario);

        Assert.Equal(name, plan.Scenario);
        Assert.Contains("--quality-boundary", plan.GeneratorArguments);
        Assert.Equal(plan.WorkerPerformancePath, plan.WorkerEnvironment["SimulationWorker__PerformanceReportPath"]);
        // Every shipped scenario can be judged: its steady interval holds the worker intervals
        // that the tick rules need.
        var steadySeconds = scenario.Generator["duration-seconds"];
        var metricsSeconds = int.Parse(
            plan.WorkerEnvironment[StressLoadPlan.MetricsIntervalSetting],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(StressLoadPlan.MetricsIntervalSeconds(steadySeconds), metricsSeconds);
        Assert.True(steadySeconds >= metricsSeconds * (StressLoadPlan.MinimumSteadyWorkerIntervals + 1));
        Assert.Equal(scenario.ShardId, plan.WorkerEnvironment["SIMULATION_SHARD_ID"]);
        Assert.Equal(scenario.WorldId, plan.WorkerEnvironment["SIMULATION_WORLD_ID"]);
    }

    [Fact]
    public void AFullStackGameplayPlanSetsTheDocumentedServiceSettings()
    {
        var plan = Plan(LoadShipped("smoke"));

        Assert.True(plan.NeedsContainers);
        Assert.True(plan.NeedsGameService);
        Assert.Equal([5500], plan.TcpPorts);
        Assert.Equal([56432, 56379], plan.ContainerTcpPorts);
        Assert.Equal([27025], plan.UdpPorts);
        Assert.Equal("http://127.0.0.1:5500", plan.GameServiceEnvironment["ASPNETCORE_URLS"]);
        Assert.Equal("Development", plan.GameServiceEnvironment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("true", plan.GameServiceEnvironment["StackStressFixtures__Enabled"]);
        Assert.Equal("1000", plan.GameServiceEnvironment["RateLimiting__Authentication__PermitLimit"]);
        Assert.Equal("false", plan.GameServiceEnvironment["DevelopmentSimulationBots__Enabled"]);
        Assert.Equal(
            plan.GameServiceEnvironment["SIMULATION_WORKER_SERVICE_SECRET"],
            plan.WorkerEnvironment["SIMULATION_WORKER_SERVICE_SECRET"]);
        Assert.Equal("127.0.0.1:56379", plan.WorkerEnvironment["ConnectionStrings__Redis"]);
        Assert.Equal("27025", plan.WorkerEnvironment["SIMULATION_WORKER_UDP_PORT"]);
        Assert.Equal("1", plan.WorkerEnvironment["SimulationWorker__ItemInteraction__DurableCorpseRefreshSeconds"]);
        Assert.Equal(
            plan.GameServiceEnvironment["STACK_STRESS_FIXTURE_SECRET"],
            plan.GeneratorEnvironment["STACK_STRESS_FIXTURE_SECRET"]);
        Assert.Contains("Database=shooter_mmo_stack_stress_test", plan.GeneratorEnvironment["STACK_STRESS_POSTGRES"], StringComparison.Ordinal);
        Assert.False(plan.WorkerEnvironment.ContainsKey("SimulationWorker__LingeringBodySeconds"));
    }

    [Fact]
    public void AWorkerOnlyPlanSharesOneEphemeralSecretAndStartsNoGameService()
    {
        var plan = Plan(LoadShipped("worker-only-500"));

        Assert.False(plan.NeedsContainers);
        Assert.False(plan.NeedsGameService);
        Assert.Empty(plan.GameServiceEnvironment);
        Assert.Equal([5099], plan.TcpPorts);
        Assert.Equal([27016], plan.UdpPorts);
        Assert.Equal("500", plan.WorkerEnvironment["SIMULATION_WORKER_MAX_CONNECTIONS"]);
        var secret = plan.WorkerEnvironment["SIMULATION_WORKER_SERVICE_SECRET"];
        Assert.True(secret.Length >= 32);
        var arguments = plan.GeneratorArguments.ToList();
        Assert.Equal(secret, arguments[arguments.IndexOf("--worker-secret") + 1]);
        Assert.Contains("--worker-secret", plan.Sensitive);
    }

    [Fact]
    public void OverridesReplaceTheBotsAndDurationAndKeepTheRampWithinTheBots()
    {
        var plan = StressLoadPlan.Create(
            LoadShipped("mixed-100"),
            new StressLoadPlanRequest(RunFolder(), BoundaryPath, Bots: 10, DurationSeconds: 30),
            RepositoryPaths.Root);

        var arguments = plan.GeneratorArguments.ToList();
        Assert.Equal("10", arguments[arguments.IndexOf("--bots") + 1]);
        Assert.Equal("30", arguments[arguments.IndexOf("--duration-seconds") + 1]);
        Assert.Equal("10", arguments[arguments.IndexOf("--ramp-step") + 1]);
    }

    [Theory]
    [InlineData("\"color\": \"red\",", "unknown property 'color'")]
    [InlineData("\"mode\": \"remote\",", "neither worker-only nor full-stack")]
    public void AScenarioWithAnUnknownPropertyOrModeIsRejected(string insert, string expected)
    {
        var json = Scenario(insert + "\n  \"mode\": \"full-stack\",", "\"bots\": 5, \"duration-seconds\": 10");
        if (insert.StartsWith("\"mode\"", StringComparison.Ordinal))
        {
            json = Scenario(insert, "\"bots\": 5, \"duration-seconds\": 10");
        }

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressLoadScenario.Parse("bad", json, "bad.json"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"bots\": 5, \"duration-seconds\": 10, \"output\": 3", "'output' is unknown or set by the plan")]
    [InlineData("\"bots\": 5, \"duration-seconds\": 10, \"botz\": 3", "'botz' is unknown")]
    [InlineData("\"bots\": \"5\", \"duration-seconds\": 10", "must be an integer")]
    [InlineData("\"duration-seconds\": 10", "'bots' is required")]
    public void AScenarioWithABadGeneratorOptionIsRejected(string generator, string expected)
    {
        var json = Scenario("\"mode\": \"full-stack\",", generator);

        var exception = Assert.Throws<StressGeneratorOptionException>(() => StressLoadScenario.Parse("bad", json, "bad.json"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScenarioCannotReplaceASettingThatThePlanOwns()
    {
        var json = Scenario(
            "\"mode\": \"full-stack\",",
            "\"bots\": 5, \"duration-seconds\": 10",
            "\"workerSettings\": { \"SIMULATION_WORKER_UDP_PORT\": \"1\" },");
        var scenario = StressLoadScenario.Parse("bad", json, "bad.json");

        var exception = Assert.Throws<StressGeneratorOptionException>(() => Plan(scenario));

        Assert.Contains("SIMULATION_WORKER_UDP_PORT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeGeneratorValueFailsThePlanAsTheGeneratorWould()
    {
        var scenario = StressLoadScenario.Parse(
            "bad",
            Scenario("\"mode\": \"full-stack\",", "\"bots\": 5, \"duration-seconds\": 10, \"http-concurrency\": 9999"),
            "bad.json");

        var exception = Assert.Throws<StressGeneratorOptionException>(() => Plan(scenario));

        Assert.Contains("--http-concurrency", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerOnlyScenariosTakeNoGameServiceSettingsAndNoGameplayWorkload()
    {
        var withSettings = Scenario(
            "\"mode\": \"worker-only\",",
            "\"bots\": 5, \"duration-seconds\": 10",
            "\"gameServiceSettings\": { \"X\": \"1\" },",
            "lifecycle");
        var withGameplay = Scenario("\"mode\": \"worker-only\",", "\"bots\": 5, \"duration-seconds\": 10", workload: "inventory");

        Assert.Throws<StressGeneratorOptionException>(() => StressLoadScenario.Parse("bad", withSettings, "bad.json"));
        Assert.Throws<StressGeneratorOptionException>(() => StressLoadScenario.Parse("bad", withGameplay, "bad.json"));
    }

    private static string BoundaryPath => Path.Combine(LoadDirectory, "quality-boundary.json");

    private static string RunFolder() => Path.Combine(Path.GetTempPath(), "load-plan-test");

    private static StressLoadScenario LoadShipped(string name)
    {
        return StressLoadScenario.Load(Path.Combine(LoadDirectory, "scenarios", name + ".json"));
    }

    private static StressLoadPlan Plan(StressLoadScenario scenario)
    {
        return StressLoadPlan.Create(
            scenario,
            new StressLoadPlanRequest(RunFolder(), BoundaryPath, SourceCommit: "abc", SourceTreeDirty: false),
            RepositoryPaths.Root);
    }

    private static string Scenario(string modeLine, string generator, string extra = "", string workload = "mixed-gameplay")
    {
        return $$"""
            {
              "description": "test",
              {{modeLine}}
              "workload": "{{workload}}",
              "shardId": "local-shard-2",
              "worldId": "development-world-1",
              {{extra}}
              "generator": { {{generator}} }
            }
            """;
    }
}
