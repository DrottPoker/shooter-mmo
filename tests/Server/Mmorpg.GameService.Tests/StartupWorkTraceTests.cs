using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Tests;

// A start with unchanged content does no work that grows with the number of characters: the
// statements of a second initialization name no table that holds a row per character or item.
// A catalog revision that the carry recompute has not completed for brings the recompute back.
public sealed partial class StartupWorkTraceTests
{
    [PostgresIntegrationFact]
    public async Task AnUnchangedStartReadsNoTableThatGrowsWithPlayers()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(heartbeatSeedShard: false);
        await context.RegisterPlayerAsync();
        await using var traced = TracedInitializer.Create(context);

        var unchanged = await traced.InitializeAsync();

        Assert.Empty(unchanged.Changes.Migrations);
        Assert.Empty(unchanged.Changes.SeedChanges);
        Assert.Empty(PlayerTableStatements(unchanged.Trace));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Unchanged start: {Statements(unchanged.Trace).Count} statements, "
            + $"{PlayerTableStatements(unchanged.Trace).Count} against player tables.");

        await context.ExecuteAsync(
            "update item_data_steps set revision = 'an-older-catalog-revision' where id = 'carry_state_recompute';");
        var changed = await traced.InitializeAsync();

        Assert.Contains(PlayerTableStatements(changed.Trace), statement =>
            statement.Contains("for update of state", StringComparison.Ordinal));
        Assert.Contains(Statements(changed.Trace), statement =>
            statement.StartsWith("insert into item_data_steps", StringComparison.Ordinal));
        Assert.Equal(
            PostgresIntegrationTestContext.FixtureItemCatalogRevision,
            await context.ExecuteScalarStringAsync(
                "select revision from item_data_steps where id = 'carry_state_recompute';"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Start after a catalog revision change: {Statements(changed.Trace).Count} statements, "
            + $"{PlayerTableStatements(changed.Trace).Count} against player tables.");
    }

    private static List<string> Statements(IReadOnlyList<string> trace)
    {
        return trace
            .Select(line => StatementPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["sql"].Value)
            .ToList();
    }

    private static List<string> PlayerTableStatements(IReadOnlyList<string> trace)
    {
        return Statements(trace).Where(statement => PlayerTablePattern().IsMatch(statement)).ToList();
    }

    // A command line of the recorder, or one statement of a batch.
    [GeneratedRegex(@"^(?:c\S+ sql |  > )(?<sql>.*)$")]
    private static partial Regex StatementPattern();

    [GeneratedRegex(@"\b(?:characters|character_item_states|character_appearances|item_instances|character_active_quests|character_quest_progress)\b")]
    private static partial Regex PlayerTablePattern();

    // The production initializer on a data source whose statements the recorder captures.
    private sealed class TracedInitializer : IAsyncDisposable
    {
        private readonly SqlTraceRecorder recorder = new();
        private readonly Microsoft.Extensions.Logging.ILoggerFactory loggerFactory;
        private readonly NpgsqlDataSource dataSource;
        private readonly ServiceProvider services;

        private TracedInitializer(PostgresIntegrationTestContext context)
        {
            loggerFactory = SqlTraceRecorder.CreateLoggerFactory(recorder);
            dataSource = new NpgsqlDataSourceBuilder(context.ConnectionString)
                .UseLoggerFactory(loggerFactory)
                .Build();
            services = GameServiceTestComposition.BuildServiceProvider(dataSource, context.GameServiceConfiguration);
        }

        public static TracedInitializer Create(PostgresIntegrationTestContext context)
        {
            return new TracedInitializer(context);
        }

        public async Task<(GameServiceDatabaseChanges Changes, IReadOnlyList<string> Trace)> InitializeAsync()
        {
            recorder.Start();
            GameServiceDatabaseChanges changes;
            IReadOnlyList<string> trace;
            try
            {
                changes = await services.GetRequiredService<GameServiceDatabaseInitializer>()
                    .InitializeAsync(CancellationToken.None);
            }
            finally
            {
                trace = recorder.Stop();
            }

            return (changes, trace);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await dataSource.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
