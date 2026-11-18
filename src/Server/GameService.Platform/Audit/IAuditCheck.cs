using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Platform.Audit;

// One read-only check of the economy audit over a module's own tables. The audit runner runs
// every check of a run in one read-only repeatable-read transaction, so all of them read the
// same snapshot, and gives each a savepoint of its own, so a check that fails leaves the others.
public interface IAuditCheck
{
    // A short, stable snake case id: the check label of the audit metrics and the key of the
    // report.
    string Id { get; }

    // One line that says what holds when the check finds nothing.
    string Description { get; }

    // Counts what the check looked at and finds its violations, in the scope's transaction. It
    // never writes and returns at most the context's example limit of examples.
    Task<AuditCheckResult> RunAsync(
        PostgresTransactionScope scope,
        AuditCheckContext context,
        CancellationToken cancellationToken);
}

// What every check of a run receives: the audit settings, among them the example limit and the
// statement timeout that bounds each statement.
public sealed record AuditCheckContext(AuditOptions Options)
{
    public int ExampleLimit => Options.ExampleLimit;
}

// What a check looked at, how many violations it found, and examples of them by id. An example
// never holds a name or anything private, only ids, numbers, and codes.
public sealed record AuditCheckResult(
    long Examined,
    long Violations,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Examples);
