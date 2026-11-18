using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Audit;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Platform.Tests;

// The audit runner with checks that stand in for the modules' checks, on a real database: one
// snapshot for every check, a read-only transaction, a statement timeout per check, and the
// report with its outcome and exit code.
public sealed class AuditRunnerTests
{
    private static readonly AuditOptions Options = AuditOptions.Default with { ExampleLimit = 2 };

    [PostgresIntegrationFact]
    public async Task EveryCheckOfARunReadsTheSameSnapshot()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await dataSource.CreateCommand("create table audited (id integer primary key); insert into audited values (1);")
            .ExecuteNonQueryAsync();
        // The first check commits a row on its own connection after it counted; the second check
        // must not see it.
        var first = new CountingCheck("first", dataSource, insertAfterCounting: 2);
        var second = new CountingCheck("second", dataSource, insertAfterCounting: null);

        var report = await AuditRunner.RunAsync(dataSource, [first, second], Options, CancellationToken.None);

        Assert.Equal(AuditOutcome.Clean, report.Outcome);
        Assert.Equal([1L, 1L], report.Checks.Select(check => check.Examined));
        Assert.Equal(2, Convert.ToInt32(await dataSource.CreateCommand("select count(*) from audited;").ExecuteScalarAsync()));
        Assert.NotNull(report.SnapshotAt);
    }

    [PostgresIntegrationFact]
    public async Task ACheckThatWritesFailsAndTheNextOneStillRuns()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await dataSource.CreateCommand("create table audited (id integer primary key);").ExecuteNonQueryAsync();
        var writer = new SqlCheck("writer", "insert into audited values (1)");
        var reader = new SqlCheck("reader", "select 1");

        var report = await AuditRunner.RunAsync(dataSource, [writer, reader], Options, CancellationToken.None);

        Assert.Equal(AuditOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.ExitCode);
        var failed = Assert.Single(report.FailedChecks);
        Assert.Equal("writer", failed.Id);
        Assert.Contains("25006", failed.Error, StringComparison.Ordinal);
        Assert.True(report.Checks.Single(check => check.Id == "reader").Ran);
        Assert.Equal(0, Convert.ToInt32(await dataSource.CreateCommand("select count(*) from audited;").ExecuteScalarAsync()));
    }

    [PostgresIntegrationFact]
    public async Task TheStatementTimeoutEndsOneCheckAndTheRunGoesOn()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var slow = new SqlCheck("slow", "select pg_sleep(5)");
        var violated = new SqlAuditCheck(
            "violated",
            "Nothing is listed below.",
            AuditSql.Counted(
                "select 3",
                "select value as row_id, 'planted' as problem from generate_series(1, 3) value",
                "row_id"));

        var report = await AuditRunner.RunAsync(
            dataSource,
            [slow, violated],
            Options with { StatementTimeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Contains("57014", Assert.Single(report.FailedChecks).Error, StringComparison.Ordinal);
        var found = Assert.Single(report.ViolatedChecks);
        Assert.Equal(3, found.Violations);
        Assert.Equal(3, found.Examined);
        // The example limit of 2 keeps the first two in the check's order, fields in column order.
        Assert.Equal(["1", "2"], found.Examples.Select(example => example["row_id"]));
        Assert.Equal(["row_id", "problem"], found.Examples[0].Keys.ToArray());
        // Violations decide the outcome over a check that could not run.
        Assert.Equal(AuditOutcome.Violations, report.Outcome);
        Assert.Equal(1, report.ExitCode);
    }

    [PostgresIntegrationFact]
    public async Task ARunThatCannotReachItsDatabaseFails()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        var missing = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        missing.Database += "_missing";
        await using var dataSource = NpgsqlDataSource.Create(missing.ConnectionString);

        var report = await AuditRunner.RunAsync(dataSource, [new SqlCheck("reader", "select 1")], Options, CancellationToken.None);

        Assert.Equal(AuditOutcome.Failed, report.Outcome);
        Assert.Equal(2, report.ExitCode);
        Assert.Null(report.SnapshotAt);
        Assert.Empty(report.Checks);
        Assert.Contains("3D000", report.Error, StringComparison.Ordinal);
    }

    // The schedule logs one line per run, an error for every check that found a violation, and a
    // warning for every check that could not run, and keeps the run in its metrics.
    [PostgresIntegrationFact]
    public async Task AScheduledRunLogsItsLineAndAnErrorPerViolatedCheck()
    {
        await using var database = await PostgresTestDatabase.LeaseAsync(PostgresTestTemplate.Empty);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var violated = new SqlAuditCheck(
            "violated",
            "Nothing is listed below.",
            AuditSql.Counted("select 5", "select 7 as row_id", "row_id"));
        var logger = new RecordingLogger();
        using var metrics = new AuditMetrics();
        var service = new AuditHostedService(
            dataSource,
            [new SqlCheck("reader", "select 1"), violated, new SqlCheck("broken", "select * from no_such_table")],
            Options,
            metrics,
            TimeProvider.System,
            new DatabaseAheadState(),
            logger);

        var report = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(AuditOutcome.Violations, report!.Outcome);
        Assert.Equal(new Dictionary<string, long> { ["reader"] = 0, ["violated"] = 1 }, metrics.CaptureViolations());
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.StartsWith(
            "Economy audit ran 2 of 3 checks in ",
            logger.Entries[0].Message,
            StringComparison.Ordinal);
        Assert.EndsWith(
            ": 1 violations in 1 checks, 1 checks could not run.",
            logger.Entries[0].Message,
            StringComparison.Ordinal);
        Assert.Equal(
            (LogLevel.Error, "Economy audit check violated found 1 violations among 5 examined: Nothing is listed below. Examples: row_id=7"),
            logger.Entries[1]);
        Assert.Equal(LogLevel.Warning, logger.Entries[2].Level);
        Assert.StartsWith("Economy audit check broken could not run: ", logger.Entries[2].Message, StringComparison.Ordinal);
        Assert.Contains("42P01", logger.Entries[2].Message, StringComparison.Ordinal);
    }

    // The checks encode this build's schema and rules, so a database that a newer release migrated
    // is not audited: the run logs why, reads nothing, and counts no run.
    [Fact]
    public async Task AScheduledRunDoesNotJudgeADatabaseThatANewerReleaseMigrated()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=audit_test;Username=test;Password=test");
        var logger = new RecordingLogger();
        using var metrics = new AuditMetrics();
        var ahead = new DatabaseAheadState();
        ahead.Record(["209912315959_from_a_newer_release"]);
        var service = new AuditHostedService(
            dataSource,
            [new SqlCheck("reader", "select 1")],
            Options,
            metrics,
            TimeProvider.System,
            ahead,
            logger);

        var report = await service.RunOnceAsync(CancellationToken.None);

        Assert.Null(report);
        Assert.Empty(metrics.CaptureViolations());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("209912315959_from_a_newer_release", entry.Message, StringComparison.Ordinal);
        Assert.Contains("newer than this build", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoChecksWithOneIdAreRefused()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=audit_test;Username=test;Password=test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AuditRunner.RunAsync(
            dataSource,
            [new SqlCheck("twice", "select 1"), new SqlCheck("twice", "select 2")],
            Options,
            CancellationToken.None));

        Assert.Contains("'twice'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportReadsAsTextAndAsJson()
    {
        var example = new OrderedDictionary<string, string?> { ["item_instance_id"] = "a1", ["problem"] = "container_without_owner", ["container_id"] = null };
        var report = new AuditReport(
            new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(152),
            [
                new AuditCheckReport("clean_check", "Nothing is wrong.", 10, 0, [], TimeSpan.FromMilliseconds(3), null),
                new AuditCheckReport("broken_check", "Every item is somewhere.", 10, 1, [example], TimeSpan.FromMilliseconds(4), null),
                new AuditCheckReport("slow_check", "It takes long.", 0, 0, [], TimeSpan.FromMilliseconds(1000), "canceling statement due to statement timeout (57014)")
            ],
            null);

        var text = AuditReportWriter.ToText(report);
        using var json = JsonDocument.Parse(AuditReportWriter.ToJson(report));

        Assert.StartsWith("Economy audit of the snapshot at 2026-11-17T10:00:00.000Z: 3 checks, 1 violations, 152 ms.", text, StringComparison.Ordinal);
        Assert.Contains("  ok        clean_check: 10 examined, 0 violations, 3 ms", text, StringComparison.Ordinal);
        Assert.Contains("  VIOLATED  broken_check: 10 examined, 1 violations, 4 ms", text, StringComparison.Ordinal);
        Assert.Contains("item_instance_id=a1, problem=container_without_owner, container_id=null", text, StringComparison.Ordinal);
        Assert.Contains("  FAILED    slow_check: canceling statement due to statement timeout (57014), 1000 ms", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "Violations found by 1 checks: broken_check. 1 checks could not run: slow_check." + Environment.NewLine,
            text,
            StringComparison.Ordinal);
        var root = json.RootElement;
        Assert.Equal("violations", root.GetProperty("outcome").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, root.GetProperty("violations").GetInt64());
        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(["clean_check", "broken_check", "slow_check"], checks.Select(check => check.GetProperty("id").GetString()));
        Assert.False(checks[2].GetProperty("ran").GetBoolean());
        var jsonExample = Assert.Single(checks[1].GetProperty("examples").EnumerateArray());
        Assert.Equal(["item_instance_id", "problem", "container_id"], jsonExample.EnumerateObject().Select(field => field.Name));
        Assert.Equal(JsonValueKind.Null, jsonExample.GetProperty("container_id").ValueKind);
    }

    [Fact]
    public void ACleanReportSaysSo()
    {
        var report = new AuditReport(
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            [new AuditCheckReport("clean_check", "Nothing is wrong.", 10, 0, [], TimeSpan.Zero, null)],
            null);

        Assert.Equal(AuditOutcome.Clean, report.Outcome);
        Assert.Equal(0, report.ExitCode);
        Assert.EndsWith("The economy is consistent." + Environment.NewLine, AuditReportWriter.ToText(report), StringComparison.Ordinal);
        Assert.Equal(2, AuditReport.NotRun("The database is not current.").ExitCode);
    }

    private sealed class RecordingLogger : ILogger<AuditHostedService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

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
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // Counts the audited rows, then commits more rows on a connection of its own.
    private sealed class CountingCheck(string id, NpgsqlDataSource dataSource, int? insertAfterCounting) : IAuditCheck
    {
        public string Id => id;

        public string Description => "Counts the audited rows.";

        public async Task<AuditCheckResult> RunAsync(
            PostgresTransactionScope scope,
            AuditCheckContext context,
            CancellationToken cancellationToken)
        {
            var count = await scope.Connection.ExecuteScalarAsync<long>(
                "select count(*) from audited;",
                transaction: scope.Transaction);
            if (insertAfterCounting is { } value)
            {
                await using var other = await dataSource.OpenConnectionAsync(cancellationToken);
                await other.ExecuteAsync("insert into audited values (@Value);", new { Value = value });
            }

            return new AuditCheckResult(count, 0, []);
        }
    }

    // Runs one statement and finds nothing.
    private sealed class SqlCheck(string id, string sql) : IAuditCheck
    {
        public string Id => id;

        public string Description => "Runs one statement.";

        public async Task<AuditCheckResult> RunAsync(
            PostgresTransactionScope scope,
            AuditCheckContext context,
            CancellationToken cancellationToken)
        {
            await scope.Connection.ExecuteAsync(sql, transaction: scope.Transaction);
            return new AuditCheckResult(1, 0, []);
        }
    }
}
