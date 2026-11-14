using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Database.Migrations;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests;

// The migrate command, its plan, and the check that a start without migrations makes.
public sealed class GameServiceDatabaseCommandTests
{
    private static readonly DatabaseMigration NextMigration = new(
        "209912310000_next_release",
        "create table next_release_test (id integer primary key);");

    [Theory]
    [InlineData(new string[0], null, new string[0])]
    [InlineData(new[] { "--urls", "http://127.0.0.1:5001" }, null, new[] { "--urls", "http://127.0.0.1:5001" })]
    [InlineData(new[] { "migrate" }, GameServiceDatabaseCommand.Migrate, new string[0])]
    [InlineData(new[] { "migrate", "--plan" }, GameServiceDatabaseCommand.Plan, new string[0])]
    [InlineData(
        new[] { "migrate", "--plan", "--ConnectionStrings:Postgres", "Host=db" },
        GameServiceDatabaseCommand.Plan,
        new[] { "--ConnectionStrings:Postgres", "Host=db" })]
    [InlineData(new[] { "migrate", "--Database:MigrationLockTimeoutMilliseconds=1000" }, GameServiceDatabaseCommand.Migrate, new[] { "--Database:MigrationLockTimeoutMilliseconds=1000" })]
    [InlineData(new[] { "--plan", "migrate" }, null, new[] { "--plan", "migrate" })]
    public void TheCommandIsTheFirstArgumentAndTheRestConfigure(
        string[] args,
        GameServiceDatabaseCommand? command,
        string[] configuration)
    {
        var parsed = GameServiceDatabaseCommandLine.Parse(args);

        Assert.Equal(command, parsed.Command);
        Assert.Equal(configuration, parsed.ConfigurationArguments);
    }

    [PostgresIntegrationFact]
    public async Task APlanOnAFreshDatabaseListsEverythingAndAMigrateRunAppliesIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);
        var schema = context.Services.GetRequiredService<GameServiceSchema>();

        var (planExit, plan, planError) = await RunAsync(context, GameServiceDatabaseCommand.Plan);
        var tablesAfterPlan = await context.ExecuteScalarIntAsync(
            "select count(*) from information_schema.tables where table_schema = 'public';");
        var (migrateExit, migrate, migrateError) = await RunAsync(context, GameServiceDatabaseCommand.Migrate);
        var (currentExit, current, _) = await RunAsync(context, GameServiceDatabaseCommand.Plan);
        var (againExit, again, _) = await RunAsync(context, GameServiceDatabaseCommand.Migrate);

        Assert.Equal(0, planExit);
        Assert.Empty(planError);
        Assert.Contains($"Pending migrations: {schema.Migrations.Count}", plan, StringComparison.Ordinal);
        Assert.All(schema.Migrations, migration => Assert.Contains(migration.Id, plan, StringComparison.Ordinal));
        Assert.Contains(
            $"Item catalog core: mirror revision {PostgresIntegrationTestContext.FixtureItemCatalogRevision}; the database holds none.",
            plan,
            StringComparison.Ordinal);
        Assert.Contains("Carry state: recompute every character", plan, StringComparison.Ordinal);
        Assert.Contains(
            $"Quest catalog core: mirror revision {PostgresIntegrationTestContext.FixtureQuestCatalogRevision}; the database holds none.",
            plan,
            StringComparison.Ordinal);
        Assert.Contains("Character looks: check every stored look", plan, StringComparison.Ordinal);
        Assert.Contains("Topology: add Shard local-shard-1 as configured.", plan, StringComparison.Ordinal);
        Assert.Contains("Run migrate without --plan to apply them.", plan, StringComparison.Ordinal);
        Assert.Equal(0, tablesAfterPlan);

        Assert.Equal(0, migrateExit);
        Assert.Empty(migrateError);
        Assert.Contains($"Applied migrations: {schema.Migrations.Count}", migrate, StringComparison.Ordinal);
        Assert.Contains("Item catalog core: mirrored revision", migrate, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine(plan);
        TestContext.Current.TestOutputHelper?.WriteLine(migrate);
        Assert.EndsWith("The database is current." + Environment.NewLine, migrate, StringComparison.Ordinal);

        Assert.Equal(0, currentExit);
        Assert.Equal(
            "Pending migrations: none" + Environment.NewLine
            + "Pending seed changes: none" + Environment.NewLine
            + "The database is current; migrate would change nothing." + Environment.NewLine,
            current);
        Assert.Equal(0, againExit);
        Assert.StartsWith("Applied migrations: none" + Environment.NewLine + "Seed changes: none", again, StringComparison.Ordinal);
        await context.DatabaseInitializer.EnsureCurrentAsync(CancellationToken.None);
    }

    [PostgresIntegrationFact]
    public async Task AStartWithoutMigrationsRefusesAPendingMigrationAndNamesTheCommand()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var initializer = InitializerWith(context, NextMigration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer.EnsureCurrentAsync(CancellationToken.None));

        Assert.Contains("Database:RunMigrationsOnStartup is false", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Pending migrations: 1" + Environment.NewLine + "  " + NextMigration.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Pending seed changes: none", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Mmorpg.GameService migrate", exception.Message, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task AStartWithoutMigrationsRefusesAMirroredCatalogThatDiffersFromTheBundledOne()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var older = new string('a', 64);
        await context.ExecuteAsync($"update quest_catalogs set revision = '{older}' where catalog_id = 'core';");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.DatabaseInitializer.EnsureCurrentAsync(CancellationToken.None));

        Assert.Contains("Pending migrations: none", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"Quest catalog core: mirror revision {PostgresIntegrationTestContext.FixtureQuestCatalogRevision}; the database holds {older}.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task AFailedMigrationPrintsTheFailureAndExitsWithOne()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var broken = new DatabaseMigration("209912310000_broken", "alter table no_such_table add column x integer;");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await GameServiceDatabaseCommandLine.RunAsync(
            GameServiceDatabaseCommand.Migrate,
            InitializerWith(context, broken),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("migrate failed: 42P01", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([broken.Id], (await InitializerWith(context, broken).PlanAsync(CancellationToken.None)).Migrations);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        PostgresIntegrationTestContext context,
        GameServiceDatabaseCommand command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await GameServiceDatabaseCommandLine.RunAsync(
            command,
            context.DatabaseInitializer,
            output,
            error,
            CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    // The production schema and seeders with one migration of a later release.
    private static GameServiceDatabaseInitializer InitializerWith(
        PostgresIntegrationTestContext context,
        DatabaseMigration migration)
    {
        var schema = GameServiceSchema.Create(
            LegacyBaselineMigrations.All,
            [.. GameServiceComposition.CreateModules(context.GameServiceConfiguration), new TestModule(migration)]);
        return new GameServiceDatabaseInitializer(
            schema,
            context.Services.GetRequiredService<DatabaseMigrationRunner>(),
            context.Services.GetServices<IDatabaseSeeder>());
    }

    private sealed class TestModule(DatabaseMigration migration) : IGameServiceModule
    {
        public string Name => "Test";

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
