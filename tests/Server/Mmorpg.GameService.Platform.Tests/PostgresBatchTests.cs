using Dapper;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Platform.Tests;

// A PostgresBatch sends its statements in one round trip, runs them in order in the caller's
// transaction, binds and reads them as Dapper commands do, and keeps the results before a
// statement that failed.
public sealed class PostgresBatchTests
{
    [PostgresIntegrationFact]
    public async Task StatementsRunInOrderInOneRoundTripAndKeepTheirResults()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        var commands = new CommandCounter();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(commands));
        await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString)
            .UseLoggerFactory(loggerFactory)
            .Build();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "create temporary table batch_rows (id int primary key, name text not null);",
            transaction: transaction);
        var batch = new PostgresBatch(new PostgresTransactionScope(connection, transaction));

        var inserted = batch.Execute(
            "insert into batch_rows (id, name) values (@First, 'one'), (@Second, 'two');",
            new { First = 1, Second = 2 });
        var name = batch.QuerySingleOrDefault<string>(
            "select name from batch_rows where id = @Id;",
            new { Id = 2 });
        var updated = batch.Execute("update batch_rows set name = name || '!';");
        var rows = batch.Query<BatchRow>("select id as \"Id\", name as \"Name\" from batch_rows order by id;");
        var missing = batch.QuerySingleOrDefault<int?>("select id from batch_rows where id = 3;");
        Assert.False(name.HasValue);
        Assert.Throws<InvalidOperationException>(() => name.Value);
        commands.Count = 0;
        await batch.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, commands.Count);
        Assert.Equal(0, batch.Count);
        Assert.Equal(2, inserted.Value);
        Assert.Equal("two", name.Value);
        Assert.Equal(2, updated.Value);
        Assert.Equal(new[] { new BatchRow(1, "one!"), new BatchRow(2, "two!") }, rows.Value);
        Assert.Null(missing.Value);
        Assert.Equal(3, name.Select(value => value!.Length).Value);

        // The batch takes new statements once it ran.
        var count = batch.QuerySingleOrDefault<long>("select count(*) from batch_rows;");
        await batch.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, count.Value);
        await transaction.RollbackAsync();
    }

    [PostgresIntegrationFact]
    public async Task AFailedStatementKeepsTheResultsBeforeItAndThrowsItsError()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "create temporary table batch_keys (id int primary key);",
            transaction: transaction);
        var batch = new PostgresBatch(new PostgresTransactionScope(connection, transaction));

        var before = batch.QuerySingleOrDefault<int>("select 7;");
        var first = batch.Execute("insert into batch_keys (id) values (1);");
        var duplicate = batch.Execute("insert into batch_keys (id) values (1);");
        var after = batch.QuerySingleOrDefault<int>("select 8;");
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => batch.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(7, before.Value);
        Assert.False(first.HasValue);
        Assert.False(duplicate.HasValue);
        Assert.False(after.HasValue);
        Assert.Throws<InvalidOperationException>(() => after.Value);
        Assert.False(PostgresBatchResult.Combine(before, after, (left, right) => left + right).HasValue);
        await transaction.RollbackAsync();
    }

    // A statement binds its parameters to the same PostgreSQL types in a batch as in a Dapper
    // command, so moving it into a batch changes nothing that it sends.
    [PostgresIntegrationFact]
    public async Task ParametersBindToTheTypesOfADapperCommand()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        object[] parameters =
        [
            new { Value = Guid.NewGuid() },
            new { Value = (Guid?)null },
            new { Value = "text" },
            new { Value = (string?)null },
            new { Value = 3 },
            new { Value = (int?)null },
            new { Value = 3L },
            new { Value = true },
            new { Value = new[] { Guid.NewGuid() } },
            new { Value = new[] { "a", "b" } },
            new { Value = new[] { 1L, 2L } },
            new { Value = DateTimeOffset.UtcNow }
        ];
        var batch = new PostgresBatch(new PostgresTransactionScope(connection, transaction));
        var batched = parameters
            .Select(parameter => batch.QuerySingleOrDefault<string>("select pg_typeof(@Value)::text;", parameter))
            .ToArray();
        var unreferenced = batch.QuerySingleOrDefault<int>(
            "select @Used;",
            new { Used = 5, Unused = new object() });
        await batch.ExecuteAsync(TestContext.Current.CancellationToken);

        for (var index = 0; index < parameters.Length; index++)
        {
            var dapper = await connection.QuerySingleAsync<string>(
                "select pg_typeof(@Value)::text;",
                parameters[index],
                transaction);
            Assert.Equal(dapper, batched[index].Value);
        }

        Assert.Equal(5, unreferenced.Value);
        await transaction.RollbackAsync();
    }

    [PostgresIntegrationFact]
    public async Task AStatementAddedWithTheWrongShapeIsRefused()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        var scope = new PostgresTransactionScope(connection, await connection.BeginTransactionAsync());

        var rowsWithoutQuery = new PostgresBatch(scope);
        rowsWithoutQuery.Execute("select 1;");
        Assert.Equal(
            "A batch statement added without rows returned rows.",
            (await Assert.ThrowsAsync<InvalidOperationException>(
                () => rowsWithoutQuery.ExecuteAsync(TestContext.Current.CancellationToken))).Message);

        var queryWithoutRows = new PostgresBatch(scope);
        queryWithoutRows.Query<int>("set local statement_timeout = 1000;");
        Assert.Equal(
            "A batch statement added as a query returned no rows.",
            (await Assert.ThrowsAsync<InvalidOperationException>(
                () => queryWithoutRows.ExecuteAsync(TestContext.Current.CancellationToken))).Message);

        var twoRows = new PostgresBatch(scope);
        twoRows.QuerySingleOrDefault<int>("select 1 union all select 2;");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => twoRows.ExecuteAsync(TestContext.Current.CancellationToken));
        await scope.Transaction.RollbackAsync();
    }

    private sealed record BatchRow(int Id, string Name);

    // Counts the commands and batches that Npgsql sends, one per round trip.
    private sealed class CommandCounter : ILoggerProvider
    {
        public int Count { get; set; }

        public ILogger CreateLogger(string categoryName)
        {
            return new CountingLogger(this, categoryName);
        }

        public void Dispose()
        {
        }

        private sealed class CountingLogger(CommandCounter counter, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category == "Npgsql.Command" && eventId.Id == 2000)
                {
                    counter.Count++;
                }
            }
        }
    }
}
