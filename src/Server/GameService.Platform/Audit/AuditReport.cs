namespace Mmorpg.GameService.Platform.Audit;

public enum AuditOutcome
{
    // Every check ran and found nothing.
    Clean,

    // At least one check found a violation; a failed check does not hide it.
    Violations,

    // Nothing was found, but the run or at least one check could not run.
    Failed
}

// One check of a run: what it looked at, what it found, and how long it took. Error says why it
// could not run; such a check counts nothing.
public sealed record AuditCheckReport(
    string Id,
    string Description,
    long Examined,
    long Violations,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Examples,
    TimeSpan Duration,
    string? Error)
{
    public bool Ran => Error is null;
}

// One run of the economy audit over one snapshot: the database time of the snapshot, the checks
// in the order they ran, and the error that ended the run early, if one did.
public sealed record AuditReport(
    DateTimeOffset? SnapshotAt,
    TimeSpan Duration,
    IReadOnlyList<AuditCheckReport> Checks,
    string? Error)
{
    public long Violations => Checks.Sum(check => check.Violations);

    public IReadOnlyList<AuditCheckReport> ViolatedChecks =>
        Checks.Where(check => check.Violations > 0).ToArray();

    public IReadOnlyList<AuditCheckReport> FailedChecks =>
        Checks.Where(check => !check.Ran).ToArray();

    public AuditOutcome Outcome => Violations > 0
        ? AuditOutcome.Violations
        : Error is not null || Checks.Any(check => !check.Ran)
            ? AuditOutcome.Failed
            : AuditOutcome.Clean;

    // The exit code of the audit command: 0 clean, 1 violations found, 2 the audit could not run.
    public int ExitCode => Outcome switch
    {
        AuditOutcome.Clean => 0,
        AuditOutcome.Violations => 1,
        _ => 2
    };

    // A run that never reached the database, such as one refused before it started.
    public static AuditReport NotRun(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new AuditReport(null, TimeSpan.Zero, [], error);
    }
}
