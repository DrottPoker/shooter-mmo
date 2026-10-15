using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Http;

namespace Mmorpg.Backend.Tests.Architecture;

// Clients, workers, and tools depend on these routes, methods, policies, rate limits,
// and no-store rules. Moving endpoints between modules must not change any of them.
public sealed class RouteTableSnapshotTests
{
    private static readonly string[] ExpectedRoutes =
    [
        "DELETE /api/accounts/sessions/{sessionId:guid} auth=AccountSession rate=- sensitive=no",
        "GET / auth=- rate=- sensitive=no",
        "GET /api/accounts/me auth=AccountSession rate=- sensitive=no",
        "GET /api/accounts/session auth=AccountSession rate=- sensitive=no",
        "GET /api/characters/ auth=AccountSession rate=- sensitive=no",
        "GET /api/characters/{characterId:guid}/bank auth=AccountSession rate=- sensitive=yes",
        "GET /api/characters/{characterId:guid}/inventory auth=AccountSession rate=- sensitive=yes",
        "GET /api/characters/{characterId:guid}/item-state auth=AccountSession rate=- sensitive=yes",
        "GET /api/characters/{characterId:guid}/recovery auth=AccountSession rate=- sensitive=yes",
        "GET /api/item-catalog auth=AccountSession rate=- sensitive=no",
        "GET /api/items/catalog auth=AccountSession rate=- sensitive=no",
        "GET /api/shards/ auth=- rate=- sensitive=no",
        "GET /api/simulation-workers/{workerId}/corpses auth=SimulationWorker rate=- sensitive=yes",
        "GET /health/live auth=- rate=- sensitive=no",
        "GET /health/ready auth=- rate=- sensitive=no",
        "POST /api/accounts/login auth=- rate=login sensitive=yes",
        "POST /api/accounts/logout auth=AccountSession rate=- sensitive=no",
        "POST /api/accounts/register auth=- rate=register sensitive=yes",
        "POST /api/characters/ auth=AccountSession rate=- sensitive=no",
        "POST /api/characters/{characterId:guid}/item-operations/destroy auth=AccountSession rate=- sensitive=yes",
        "POST /api/characters/{characterId:guid}/item-operations/merge auth=AccountSession rate=- sensitive=yes",
        "POST /api/characters/{characterId:guid}/item-operations/relocate auth=AccountSession rate=- sensitive=yes",
        "POST /api/characters/{characterId:guid}/item-operations/split auth=AccountSession rate=- sensitive=yes",
        "POST /api/development/simulation-bots/join-tickets auth=- rate=- sensitive=yes",
        "POST /api/development/stack-stress/characters/{characterId:guid}/inventory-fixture auth=AccountSession rate=- sensitive=yes",
        "POST /api/development/stack-stress/loot-hotspot auth=AccountSession rate=- sensitive=yes",
        "POST /api/items/secure-container-tier auth=AccountSession rate=- sensitive=yes",
        "POST /api/shards/{shardId}/join auth=AccountSession rate=- sensitive=yes",
        "POST /api/simulation-join-tickets/consume auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/corpses/{corpseId:guid}/item-operations auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/corpses/{corpseId:guid}/open auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/heartbeat auth=SimulationWorker rate=- sensitive=no",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/item-operations auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/mob-loot-grants auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/player-deaths auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-sessions/{simulationSessionId:guid}/release auth=SimulationWorker rate=- sensitive=no",
        "POST /api/simulation-workers/{workerId}/heartbeat auth=SimulationWorker rate=- sensitive=no",
        "POST /api/simulation-workers/{workerId}/mob-corpses/durable auth=SimulationWorker rate=- sensitive=yes",
        "POST /api/simulation-workers/{workerId}/offline auth=SimulationWorker rate=- sensitive=no"
    ];

    [Fact]
    public async Task GameServiceRouteTableIsUnchanged()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(Settings());
        var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);
        builder.Services.AddGameService(configuration);
        await using var app = builder.Build();
        app.MapGameService(configuration);

        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedRoutes, actual);
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
            ?? [];
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy ?? "(default)")
            .ToArray();
        var rateLimitPolicy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var sensitive = endpoint.Metadata.GetMetadata<SensitiveResponseAttribute>() is not null;
        return string.Join(
            ' ',
            string.Join(',', methods),
            endpoint.RoutePattern.RawText,
            $"auth={(policies.Length == 0 ? "-" : string.Join(',', policies))}",
            $"rate={rateLimitPolicy ?? "-"}",
            $"sensitive={(sensitive ? "yes" : "no")}");
    }

    private static Dictionary<string, string?> Settings()
    {
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] =
                "Host=localhost;Port=5432;Database=route_snapshot_test;Username=test;Password=test",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["HealthChecks:TimeoutMilliseconds"] = "1000",
            ["Database:RunMigrationsOnStartup"] = "false",
            ["Database:MaximumPoolSize"] = "8",
            ["Auth:SessionLifetimeHours"] = "24",
            ["Game:MaxCharactersPerAccount"] = "5",
            ["Simulation:JoinTicketLifetimeSeconds"] = "30",
            ["Simulation:SessionLeaseLifetimeSeconds"] = "30",
            ["Simulation:WorkerHeartbeatTimeoutSeconds"] = "30",
            ["Simulation:Topology:Fleets:0:Id"] = "local-fleet",
            ["Simulation:Topology:Fleets:0:DisplayName"] = "Local Development",
            ["Simulation:Topology:Fleets:0:RegionCode"] = "LOCAL",
            ["Simulation:Topology:Nodes:0:Id"] = "local-node-1",
            ["Simulation:Topology:Nodes:0:FleetId"] = "local-fleet",
            ["Simulation:Topology:Nodes:0:DisplayName"] = "Local Node 1",
            ["Simulation:Topology:Shards:0:Id"] = "local-shard-1",
            ["Simulation:Topology:Shards:0:FleetId"] = "local-fleet",
            ["Simulation:Topology:Shards:0:DisplayName"] = "Local Shard 1",
            ["Simulation:Topology:Shards:0:RuleSet"] = "mvp-open-risk",
            ["RateLimiting:Authentication:PermitLimit"] = "5",
            ["RateLimiting:Authentication:WindowSeconds"] = "60",
            ["ServiceAuthentication:SimulationWorkers:local-simulation-worker-1"] =
                "test-simulation-worker-secret-at-least-32-characters",
            ["WorldData:WorldsPath"] = RepositoryPaths.Worlds,
            ["DevelopmentSimulationBots:Enabled"] = "true",
            ["ACTIVE_SIMULATION_BOTS_SECRET"] = "test-active-simulation-bots-secret-at-least-32-characters",
            ["StackStressFixtures:Enabled"] = "true",
            ["STACK_STRESS_FIXTURE_SECRET"] = "test-stack-stress-fixture-secret-at-least-32-characters"
        };
    }
}
