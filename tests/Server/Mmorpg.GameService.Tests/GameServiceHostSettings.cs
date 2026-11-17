using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Tests;

// The whole GameService host as Program.cs builds it, on a loopback port, without its
// background services. The routes that the tests call answer before they reach PostgreSQL.
internal static class GameServiceHostSettings
{
    public const string WorkerSecret = "test-simulation-worker-secret-at-least-32-characters";
    public const string BotSecret = "test-active-simulation-bots-secret-at-least-32-characters";

    // Every required setting, valid in any environment.
    public static Dictionary<string, string?> Required()
    {
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] =
                "Host=127.0.0.1;Port=1;Database=host_test;Username=test;Password=a-real-postgres-secret",
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,password=a-real-redis-secret",
            ["HealthChecks:TimeoutMilliseconds"] = "1000",
            ["Database:RunMigrationsOnStartup"] = "false",
            ["Database:MaximumPoolSize"] = "8",
            ["Auth:SessionIdleLifetimeDays"] = "14",
            ["Auth:SessionMaximumLifetimeDays"] = "90",
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
            ["ServiceAuthentication:SimulationWorkers:local-simulation-worker-1"] = WorkerSecret,
            ["WorldData:WorldsPath"] = RepositoryPaths.Worlds,
            ["Items:NpcLifecycle:InsurancePrice"] = "100",
            ["AllowedHosts"] = "localhost;127.0.0.1"
        };
    }

    public static async Task<WebApplication> StartAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);
        builder.ConfigureGameServiceHost(configuration);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var dataSource = NpgsqlDataSource.Create(configuration.Platform.PostgresConnectionString);
        builder.Services.AddGameServiceForTests(dataSource, configuration);

        var application = builder.Build();
        application.UseGameServicePipeline();
        application.MapGameService(configuration);
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    public static HttpClient CreateClient(WebApplication application)
    {
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
