using Npgsql;

namespace Mmorpg.GameService.Platform.Audit;

// Runs the economy audit at start and then every Audit:IntervalSeconds, one run at a time, when
// Audit:Enabled is on. Each run logs one line, an error for every check that found a violation,
// and a warning for every check that could not run, and updates the audit metrics. Several
// GameService processes each run their own audit; it only reads.
internal sealed class AuditHostedService(
    NpgsqlDataSource dataSource,
    IEnumerable<IAuditCheck> checks,
    AuditOptions options,
    AuditMetrics metrics,
    TimeProvider timeProvider,
    ILogger<AuditHostedService> logger) : BackgroundService
{
    private readonly IAuditCheck[] checks = checks.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("The economy audit does not run on a schedule; Audit:Enabled turns it on.");
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(options.Interval, timeProvider);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<AuditReport?> RunOnceAsync(CancellationToken cancellationToken)
    {
        AuditReport report;
        try
        {
            report = await AuditRunner.RunAsync(dataSource, checks, options, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Two checks with one id; the next run meets the same, but the service keeps serving.
            logger.LogError(exception, "Economy audit could not start.");
            return null;
        }

        metrics.RecordRun(report, timeProvider.GetUtcNow());
        Log(report);
        return report;
    }

    private void Log(AuditReport report)
    {
        logger.LogInformation(
            "Economy audit ran {AuditChecks} of {AuditCheckCount} checks in {DurationMilliseconds:0} ms: {AuditViolations} violations in {ViolatedChecks} checks, {FailedChecks} checks could not run.",
            report.Checks.Count(check => check.Ran),
            checks.Length,
            report.Duration.TotalMilliseconds,
            report.Violations,
            report.ViolatedChecks.Count,
            report.FailedChecks.Count + (report.Error is null ? 0 : checks.Length - report.Checks.Count));
        foreach (var check in report.ViolatedChecks)
        {
            logger.LogError(
                "Economy audit check {AuditCheck} found {AuditViolations} violations among {AuditExamined} examined: {AuditCheckDescription} Examples: {AuditExamples}",
                check.Id,
                check.Violations,
                check.Examined,
                check.Description,
                string.Join("; ", check.Examples.Select(AuditReportWriter.FormatExample)));
        }

        foreach (var check in report.FailedChecks)
        {
            logger.LogWarning(
                "Economy audit check {AuditCheck} could not run: {AuditError}",
                check.Id,
                check.Error);
        }

        if (report.Error is not null)
        {
            logger.LogWarning("Economy audit could not run every check: {AuditError}", report.Error);
        }
    }
}
