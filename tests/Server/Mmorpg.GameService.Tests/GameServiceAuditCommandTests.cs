using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Database.Migrations;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Platform.Audit;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Tests;

// The audit command beside migrate: its arguments, its report in both forms, its exit codes, and
// the Audit settings that the host loads.
public sealed class GameServiceAuditCommandTests
{
    [Theory]
    [InlineData(new[] { "audit" }, GameServiceDatabaseCommand.Audit, new string[0])]
    [InlineData(new[] { "audit", "--json" }, GameServiceDatabaseCommand.AuditJson, new string[0])]
    [InlineData(
        new[] { "audit", "--json", "--Audit:StatementTimeoutSeconds", "5" },
        GameServiceDatabaseCommand.AuditJson,
        new[] { "--Audit:StatementTimeoutSeconds", "5" })]
    [InlineData(new[] { "audit", "--Audit:ExampleLimit=1" }, GameServiceDatabaseCommand.Audit, new[] { "--Audit:ExampleLimit=1" })]
    [InlineData(new[] { "--json", "audit" }, null, new[] { "--json", "audit" })]
    public void TheAuditIsACommandOfItsOwn(
        string[] args,
        GameServiceDatabaseCommand? command,
        string[] configuration)
    {
        var parsed = GameServiceDatabaseCommandLine.Parse(args);

        Assert.Equal(command, parsed.Command);
        Assert.Equal(configuration, parsed.ConfigurationArguments);
    }

    [PostgresIntegrationFact]
    public async Task ACleanDatabaseExitsWithZeroInBothForms()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await context.RegisterPlayerAsync();

        var (textExit, text, textError) = await RunAsync(context, GameServiceDatabaseCommand.Audit);
        var (jsonExit, json, jsonError) = await RunAsync(context, GameServiceDatabaseCommand.AuditJson);

        Assert.Equal(0, textExit);
        Assert.Empty(textError);
        Assert.StartsWith("Economy audit of the snapshot at ", text, StringComparison.Ordinal);
        Assert.EndsWith("The economy is consistent." + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Equal(0, jsonExit);
        Assert.Empty(jsonError);
        using var report = JsonDocument.Parse(json);
        Assert.Equal("clean", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            [.. CharactersAudit.Checks.Select(check => check.Id), .. ItemsAudit.Checks.Select(check => check.Id)],
            report.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("id").GetString()).ToArray());
    }

    [PostgresIntegrationFact]
    public async Task AViolationExitsWithOneAndNamesItsCheck()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await context.ExecuteAsync(
            $"update characters set currency = currency + 3 where id = '{player.Character.Id}';");

        var (textExit, text, _) = await RunAsync(context, GameServiceDatabaseCommand.Audit);
        var (jsonExit, json, _) = await RunAsync(context, GameServiceDatabaseCommand.AuditJson);

        Assert.Equal(1, textExit);
        Assert.Contains("  VIOLATED  currency_balance_matches_ledger: 1 examined, 1 violations", text, StringComparison.Ordinal);
        Assert.Contains($"character_id={player.Character.Id}", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "Violations found by 1 checks: currency_balance_matches_ledger." + Environment.NewLine,
            text,
            StringComparison.Ordinal);
        Assert.Equal(1, jsonExit);
        using var report = JsonDocument.Parse(json);
        Assert.Equal("violations", report.RootElement.GetProperty("outcome").GetString());
        var violated = Assert.Single(
            report.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("violations").GetInt64() > 0);
        Assert.Equal("currency_balance_matches_ledger", violated.GetProperty("id").GetString());
    }

    [PostgresIntegrationFact]
    public async Task ADatabaseThatMigrateWouldChangeIsNotAuditedAndExitsWithTwo()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var next = new DatabaseMigration("209912310000_next_release", "create table next_release_test (id integer primary key);");
        var schema = GameServiceSchema.Create(
            LegacyBaselineMigrations.All,
            [.. GameServiceComposition.CreateModules(context.GameServiceConfiguration), new NextReleaseModule(next)]);
        var initializer = new GameServiceDatabaseInitializer(
            schema,
            context.Services.GetRequiredService<DatabaseMigrationRunner>(),
            context.Services.GetServices<IDatabaseSeeder>());
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await GameServiceAuditCommand.RunAsync(
            json: false,
            initializer,
            context.Services.GetRequiredService<NpgsqlDataSource>(),
            context.Services.GetServices<IAuditCheck>(),
            AuditOptions.Default,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("audit failed: The database is not current", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Mmorpg.GameService migrate", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(next.Id, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("The audit could not run every check: The database is not current", output.ToString(), StringComparison.Ordinal);
    }

    // The scheduled audit is off in Development and on elsewhere, and the command reads the same
    // Audit settings as a start.
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public void TheHostLoadsTheAuditSettingsForItsEnvironment(string environment, bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(Settings());

        var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);

        Assert.Equal(enabled, configuration.Platform.Audit.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(30), configuration.Platform.Audit.Interval);
        Assert.Equal(AuditOptions.Default.PendingOperationAge, configuration.Platform.Audit.PendingOperationAge);
        Assert.Equal(Environments.Development == environment, builder.Environment.IsDevelopment());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        PostgresIntegrationTestContext context,
        GameServiceDatabaseCommand command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await GameServiceDatabaseCommandLine.RunAsync(
            command,
            context.Services,
            output,
            error,
            CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static Dictionary<string, string?> Settings()
    {
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] =
                "Host=localhost;Port=5432;Database=audit_settings_test;Username=test;Password=test",
            ["ConnectionStrings:Redis"] = "localhost:6379",
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
            ["ServiceAuthentication:SimulationWorkers:local-simulation-worker-1"] =
                "test-simulation-worker-secret-at-least-32-characters",
            ["WorldData:WorldsPath"] = RepositoryPaths.Worlds,
            ["Items:NpcLifecycle:InsurancePrice"] = "100",
            ["Items:NpcLifecycle:QuestGrantId"] = "quest.fixture.letter",
            ["Items:NpcLifecycle:QuestItemDefinitionId"] = "fixture.letter",
            ["Items:NpcLifecycle:QuestItemQuantity"] = "1",
            [AuditOptions.IntervalSecondsKey] = "1800"
        };
    }

    private sealed class NextReleaseModule(DatabaseMigration migration) : IGameServiceModule
    {
        public string Name => "NextRelease";

        public IReadOnlyList<DatabaseMigration> Migrations { get; } = [migration];

        public IReadOnlyList<string> MeterNames { get; } = [];

        public void AddServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}
