using Mmorpg.GameService.Platform.Audit;
using Npgsql;

namespace Mmorpg.GameService.Database;

// `audit` and `audit --json`: one run of the economy audit with the service's checks and Audit
// settings, whatever Audit:Enabled says. The checks compare the data with the current schema and
// the catalogs the service runs with, so a database that migrate would still change is refused.
// Exit codes: 0 clean, 1 violations found, 2 the audit could not run.
internal static class GameServiceAuditCommand
{
    public static async Task<int> RunAsync(
        bool json,
        GameServiceDatabaseInitializer initializer,
        NpgsqlDataSource dataSource,
        IEnumerable<IAuditCheck> checks,
        AuditOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        AuditReport report;
        try
        {
            var plan = await initializer.PlanAsync(cancellationToken);
            report = plan.IsEmpty
                ? await AuditRunner.RunAsync(dataSource, checks, options, cancellationToken)
                : AuditReport.NotRun(
                    "The database is not current, and the audit checks only a current one. "
                    + GameServiceDatabaseCommandLine.MigrateAdvice + Environment.NewLine
                    + plan.Describe("Pending migrations", "Pending seed changes").TrimEnd());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report = AuditReport.NotRun(GameServiceDatabaseCommandLine.Describe(exception));
        }

        if (json)
        {
            await output.WriteLineAsync(AuditReportWriter.ToJson(report));
        }
        else
        {
            await output.WriteAsync(AuditReportWriter.ToText(report));
        }

        if (report.Error is not null)
        {
            await error.WriteLineAsync("audit failed: " + report.Error);
        }

        return report.ExitCode;
    }
}
