using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Platform.Audit;

// An audit check that is one statement of AuditSql.Counted over the module's own tables, with
// parameters that may come from the audit settings. The module that owns the tables defines it.
public sealed class SqlAuditCheck : IAuditCheck
{
    private readonly Func<AuditOptions, object?>? parameters;

    public SqlAuditCheck(
        string id,
        string description,
        string sql,
        Func<AuditOptions, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Id = id;
        Description = description;
        Sql = sql;
        this.parameters = parameters;
    }

    public string Id { get; }

    public string Description { get; }

    // The statement, for a query plan or a measurement of its cost.
    public string Sql { get; }

    public object? ParametersFor(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return parameters?.Invoke(options);
    }

    public Task<AuditCheckResult> RunAsync(
        PostgresTransactionScope scope,
        AuditCheckContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AuditSql.RunAsync(scope, context, Sql, ParametersFor(context.Options), cancellationToken);
    }
}
