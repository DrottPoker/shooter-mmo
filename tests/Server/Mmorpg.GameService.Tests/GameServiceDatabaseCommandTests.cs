using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private const string NewerRelease = "209912315959_from_a_newer_release";

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

    // The option counts at any position after the verb. It used to count only as the second
    // argument, so a plan with a configuration argument before it ran the real migration.
    [Theory]
    [InlineData(
        new[] { "migrate", "--ConnectionStrings:Postgres=Host=db", "--plan" },
        GameServiceDatabaseCommand.Plan,
        new[] { "--ConnectionStrings:Postgres=Host=db" })]
    [InlineData(
        new[] { "migrate", "--ConnectionStrings:Postgres", "Host=db", "--plan" },
        GameServiceDatabaseCommand.Plan,
        new[] { "--ConnectionStrings:Postgres", "Host=db" })]
    [InlineData(
        new[] { "migrate", "/Retention:BatchSize=5", "--plan", "Retention:IntervalSeconds=9" },
        GameServiceDatabaseCommand.Plan,
        new[] { "/Retention:BatchSize=5", "Retention:IntervalSeconds=9" })]
    [InlineData(
        new[] { "migrate", "--environment", "Production", "--plan", "--plan" },
        GameServiceDatabaseCommand.Plan,
        new[] { "--environment", "Production" })]
    [InlineData(
        new[] { "migrate", "/Database:RunMigrationsOnStartup", "/data/state", "--urls=http://127.0.0.1:5001" },
        GameServiceDatabaseCommand.Migrate,
        new[] { "/Database:RunMigrationsOnStartup", "/data/state", "--urls=http://127.0.0.1:5001" })]
    public void TheOptionCountsAtAnyPositionAfterTheVerb(
        string[] args,
        GameServiceDatabaseCommand command,
        string[] configuration)
    {
        var parsed = GameServiceDatabaseCommandLine.Parse(args);

        Assert.Equal(command, parsed.Command);
        Assert.Equal(configuration, parsed.ConfigurationArguments);
    }

    // The configuration's command line provider ignores a bare word and a trailing switch, so each
    // of these would have run the real migration. They are refused before anything connects.
    [Theory]
    [InlineData(new[] { "migrate", "--Plan" }, "--Plan")]
    [InlineData(new[] { "migrate", "--PLAN" }, "--PLAN")]
    [InlineData(new[] { "migrate", "--plan=true" }, "--plan=true")]
    [InlineData(new[] { "migrate", "--plan", "true" }, "'true'")]
    [InlineData(new[] { "migrate", "-plan" }, "-plan")]
    [InlineData(new[] { "migrate", "/plan" }, "/plan")]
    [InlineData(new[] { "migrate", "plan" }, "'plan'")]
    [InlineData(new[] { "migrate", "plan=true" }, "plan=true")]
    [InlineData(new[] { "migrate", "--dry-run" }, "--dry-run")]
    [InlineData(new[] { "migrate", "--Foo", "--plan" }, "'--Foo'")]
    [InlineData(new[] { "migrate", "--dry-run", "--Database:MigrationLockTimeoutMilliseconds=1000" }, "--dry-run")]
    [InlineData(new[] { "migrate", "--dry-run", "/Database:MaximumPoolSize=5" }, "--dry-run")]
    [InlineData(new[] { "migrate", "--ConnectionStrings:Postgres=Host=db", "--dry-run" }, "--dry-run")]
    [InlineData(new[] { "migrate", "--json" }, "'--json'")]
    [InlineData(new[] { "migrate", "-p" }, "'-p'")]
    [InlineData(new[] { "migrate", "-k=v" }, "'-k=v'")]
    [InlineData(new[] { "migrate", "" }, "''")]
    [InlineData(new[] { "migrate", "--=value" }, "'--=value'")]
    [InlineData(new[] { "audit", "--plan" }, "'--plan'")]
    [InlineData(new[] { "audit", "--Json" }, "--Json")]
    [InlineData(new[] { "audit", "--json=true" }, "--json=true")]
    [InlineData(new[] { "audit", "--jsno" }, "--jsno")]
    public void ALineThatTheConfigurationWouldReadWrongIsRefusedBeforeItRuns(string[] args, string named)
    {
        var exception = Assert.Throws<GameServiceDatabaseCommandLineException>(
            () => GameServiceDatabaseCommandLine.Parse(args));

        Assert.Contains(named, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{args[0]} did not run", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"Usage: Mmorpg.GameService {args[0]}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
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

    // A newer release migrated the database and the older build is started against it, which a
    // rollback of the code does. The migrate command must not mirror the older catalogs over the
    // newer release's, so it and its plan refuse before they apply or seed anything.
    [PostgresIntegrationFact]
    public async Task ADatabaseThatANewerReleaseMigratedIsRefusedByMigrateAndItsPlan()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var older = new string('a', 64);
        await context.ExecuteAsync($"update quest_catalogs set revision = '{older}' where catalog_id = 'core';");
        await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        var initializer = InitializerWith(context, NextMigration);

        var migrate = await RunAsync(initializer, GameServiceDatabaseCommand.Migrate);
        var plan = await RunAsync(initializer, GameServiceDatabaseCommand.Plan);

        Assert.Equal(1, migrate.ExitCode);
        Assert.Empty(migrate.Output);
        Assert.StartsWith("migrate failed: ", migrate.Error, StringComparison.Ordinal);
        Assert.Contains(NewerRelease, migrate.Error, StringComparison.Ordinal);
        Assert.Contains("newer than this build", migrate.Error, StringComparison.Ordinal);
        Assert.Equal(1, plan.ExitCode);
        Assert.Empty(plan.Output);
        Assert.StartsWith("migrate --plan failed: ", plan.Error, StringComparison.Ordinal);
        Assert.Contains(NewerRelease, plan.Error, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from schema_migrations where id = '{NextMigration.Id}';"));
        Assert.Equal(
            older,
            await context.ExecuteScalarStringAsync("select revision from quest_catalogs where catalog_id = 'core';"));
    }

    // Two releases migrate at once: the older build must not seed after the newer one finished.
    // A migrate-and-seed holds the operation lock, so the older build waits for the newer release
    // and then reads its ids, instead of deciding from a read it made before.
    [PostgresIntegrationFact]
    public async Task AMigrateThatWaitedForANewerReleaseSeesItsMigrationsAndRefuses()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var older = new string('a', 64);
        var runner = context.Services.GetRequiredService<DatabaseMigrationRunner>();
        var initializer = InitializerWith(context, NextMigration);

        Task<(int ExitCode, string Output, string Error)> migrate;
        await using (await runner.HoldOperationLockAsync(TestContext.Current.CancellationToken))
        {
            // The newer release is in the middle of its migrate-and-seed.
            migrate = Task.Run(() => RunAsync(initializer, GameServiceDatabaseCommand.Migrate), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            Assert.False(migrate.IsCompleted, "The older build's migrate waits while another migrate-and-seed runs.");
            await context.ExecuteAsync($"update quest_catalogs set revision = '{older}' where catalog_id = 'core';");
            await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        }

        var result = await migrate;

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(NewerRelease, result.Error, StringComparison.Ordinal);
        Assert.Contains("newer than this build", result.Error, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from schema_migrations where id = '{NextMigration.Id}';"));
        Assert.Equal(
            older,
            await context.ExecuteScalarStringAsync("select revision from quest_catalogs where catalog_id = 'core';"));
    }

    // The expand rule promises that the version before runs on the next schema, so a start without
    // migrations goes on while nothing else is pending, and says so once.
    [PostgresIntegrationFact]
    public async Task AStartWithoutMigrationsRunsOnADatabaseThatANewerReleaseMigratedAndWarnsOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        var logger = new RecordingLogger();

        var unknown = await context.DatabaseInitializer.EnsureCurrentAsync(CancellationToken.None);
        await context.DatabaseInitializer.PrepareForStartAsync(false, logger, CancellationToken.None);

        Assert.Equal([NewerRelease], unknown);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(NewerRelease, warning, StringComparison.Ordinal);
        Assert.Contains("newer than this build", warning, StringComparison.Ordinal);
    }

    // The Development convenience keeps its behavior on such a database, where a developer
    // switches branches: it migrates and seeds as before, and logs the same warning.
    [PostgresIntegrationFact]
    public async Task AStartWithMigrationsStillMigratesADatabaseThatANewerReleaseMigratedAndWarnsOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        var initializer = InitializerWith(context, NextMigration);
        var logger = new RecordingLogger();

        await initializer.PrepareForStartAsync(true, logger, CancellationToken.None);

        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from schema_migrations where id = '{NextMigration.Id}';"));
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(NewerRelease, warning, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task ACurrentDatabaseStartsWithoutAWarning()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        var logger = new RecordingLogger();

        await context.DatabaseInitializer.PrepareForStartAsync(false, logger, CancellationToken.None);
        await context.DatabaseInitializer.PrepareForStartAsync(true, logger, CancellationToken.None);

        Assert.Empty(logger.Warnings);
    }

    // When such a database also has a pending migration or a seed difference, the start refuses,
    // and the migrate command that the usual message advises would refuse it too.
    [PostgresIntegrationFact]
    public async Task AStartThatRefusesADatabaseThatANewerReleaseMigratedDoesNotAdviseMigrate()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        var older = new string('a', 64);

        var pending = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InitializerWith(context, NextMigration).EnsureCurrentAsync(CancellationToken.None));
        await context.ExecuteAsync($"update quest_catalogs set revision = '{older}' where catalog_id = 'core';");
        var seedDifference = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.DatabaseInitializer.EnsureCurrentAsync(CancellationToken.None));

        Assert.Contains("Pending migrations: 1" + Environment.NewLine + "  " + NextMigration.Id, pending.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"Quest catalog core: mirror revision {PostgresIntegrationTestContext.FixtureQuestCatalogRevision}; the database holds {older}.",
            seedDifference.Message,
            StringComparison.Ordinal);
        foreach (var message in new[] { pending.Message, seedDifference.Message })
        {
            Assert.Contains("newer than this build", message, StringComparison.Ordinal);
            Assert.Contains(NewerRelease, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Run the migrate command", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Mmorpg.GameService migrate", message, StringComparison.Ordinal);
            Assert.DoesNotContain("migrate --plan", message, StringComparison.Ordinal);
        }
    }

    // The audit checks this build's schema and catalog against the data, so it does not judge a
    // database that a newer release migrated.
    [PostgresIntegrationFact]
    public async Task TheAuditDoesNotJudgeADatabaseThatANewerReleaseMigrated()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await context.ExecuteAsync($"insert into schema_migrations (id) values ('{NewerRelease}');");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await GameServiceDatabaseCommandLine.RunAsync(
            GameServiceDatabaseCommand.Audit,
            context.Services,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("audit failed: ", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("newer than this build", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(NewerRelease, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("VIOLATED", output.ToString(), StringComparison.Ordinal);
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

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        GameServiceDatabaseInitializer initializer,
        GameServiceDatabaseCommand command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await GameServiceDatabaseCommandLine.RunAsync(
            command,
            initializer,
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

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
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
