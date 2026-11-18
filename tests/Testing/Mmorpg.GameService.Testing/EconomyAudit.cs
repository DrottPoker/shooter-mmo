using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Testing;

// Runs the economy audit with every module's checks over a test's database, as the service's
// schedule and the audit command do. Scenario tests end with AssertCleanAsync; the tests that
// plant a violation read which checks report it.
internal static class EconomyAudit
{
    // A scenario has ended when it audits, so no item operation may be pending at all.
    public static AuditOptions Options { get; } = AuditOptions.Default with
    {
        PendingOperationAge = TimeSpan.Zero,
        ExampleLimit = 10
    };

    public static Task<AuditReport> RunAsync(
        PostgresIntegrationTestContext context,
        AuditOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AuditRunner.RunAsync(
            context.DataSource,
            context.Services.GetServices<IAuditCheck>(),
            options ?? Options,
            CancellationToken.None);
    }

    public static async Task AssertCleanAsync(PostgresIntegrationTestContext context)
    {
        var report = await RunAsync(context);
        Assert.True(report.Outcome == AuditOutcome.Clean, AuditReportWriter.ToText(report));
    }

    // Requires that every check ran and that exactly the named check found violations, and
    // returns its report.
    public static async Task<AuditCheckReport> AssertOnlyViolationAsync(
        PostgresIntegrationTestContext context,
        string checkId,
        AuditOptions? options = null)
    {
        var report = await RunAsync(context, options);
        Assert.True(report.FailedChecks.Count == 0 && report.Error is null, AuditReportWriter.ToText(report));
        Assert.True(
            report.ViolatedChecks.Select(check => check.Id).SequenceEqual([checkId]),
            AuditReportWriter.ToText(report));
        return report.ViolatedChecks[0];
    }
}
