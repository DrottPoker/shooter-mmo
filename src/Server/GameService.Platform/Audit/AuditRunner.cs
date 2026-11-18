using System.Data;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Platform.Audit;

// Runs the checks of one economy audit in one read-only repeatable-read transaction: every check
// reads the same snapshot, so operations that commit meanwhile cause no false alarm, and the
// transaction can write nothing. Each check runs under a savepoint, so a check that fails, such
// as one that hits the statement timeout, is reported and the next one still runs. The service's
// schedule, the audit command, the load tool, and the tests all run the audit through here.
public static class AuditRunner
{
    // The audit takes only the share locks of its reads, which wait only for schema changes; it
    // gives up on such a wait instead of queueing everything behind it.
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    private const string CheckSavepoint = "audit_check";

    public static async Task<AuditReport> RunAsync(
        NpgsqlDataSource dataSource,
        IEnumerable<IAuditCheck> checks,
        AuditOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(options);
        var ordered = checks.ToArray();
        var duplicate = ordered
            .GroupBy(check => check.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two audit checks have the id '{duplicate.Key}'; the id is the label of their metrics.");
        }

        var started = Stopwatch.GetTimestamp();
        var reports = new List<AuditCheckReport>(ordered.Length);
        DateTimeOffset? snapshotAt = null;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select
                    set_config('statement_timeout', @StatementTimeout, true),
                    set_config('lock_timeout', @LockTimeout, true);
                """,
                new
                {
                    StatementTimeout = Milliseconds(options.StatementTimeout),
                    LockTimeout = Milliseconds(LockTimeout)
                },
                transaction,
                cancellationToken: cancellationToken));
            // now() is the start of the transaction, the moment of the snapshot that every check reads.
            var databaseTime = await connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                "select now();",
                transaction: transaction,
                cancellationToken: cancellationToken));
            snapshotAt = new DateTimeOffset(DateTime.SpecifyKind(databaseTime, DateTimeKind.Utc));

            var scope = new PostgresTransactionScope(connection, transaction);
            var context = new AuditCheckContext(options);
            foreach (var check in ordered)
            {
                reports.Add(await RunCheckAsync(connection, transaction, scope, check, context, cancellationToken));
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new AuditReport(snapshotAt, Stopwatch.GetElapsedTime(started), reports, Describe(exception));
        }

        return new AuditReport(snapshotAt, Stopwatch.GetElapsedTime(started), reports, null);
    }

    private static async Task<AuditCheckReport> RunCheckAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresTransactionScope scope,
        IAuditCheck check,
        AuditCheckContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await transaction.SaveAsync(CheckSavepoint, cancellationToken);
        try
        {
            var result = await check.RunAsync(scope, context, cancellationToken);
            await transaction.ReleaseAsync(CheckSavepoint, cancellationToken);
            return new AuditCheckReport(
                check.Id,
                check.Description,
                result.Examined,
                result.Violations,
                result.Examples.Take(context.ExampleLimit).ToArray(),
                Stopwatch.GetElapsedTime(started),
                null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && connection.State == ConnectionState.Open)
        {
            // The savepoint keeps the snapshot and lets the next check run after a failed one.
            await transaction.RollbackAsync(CheckSavepoint, cancellationToken);
            return new AuditCheckReport(
                check.Id,
                check.Description,
                0,
                0,
                [],
                Stopwatch.GetElapsedTime(started),
                Describe(exception));
        }
    }

    internal static string Describe(Exception exception)
    {
        return exception is PostgresException postgres
            ? $"{postgres.MessageText} ({postgres.SqlState})"
            : exception.Message;
    }

    private static string Milliseconds(TimeSpan value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:0}ms");
    }
}
