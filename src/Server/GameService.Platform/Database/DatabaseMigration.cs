using System.Text.RegularExpressions;

namespace Mmorpg.GameService.Platform.Database;

// One schema migration. The id is recorded in schema_migrations, so an applied
// migration's id and SQL never change. A migration runs in its own transaction, except one
// that builds an index concurrently, which PostgreSQL runs only outside a transaction.
public sealed partial record DatabaseMigration(string Id, string Sql)
{
    // The index that a migration outside a transaction builds, or null for a migration that
    // runs in a transaction. A failed concurrent build leaves this index invalid, and the
    // runner drops it before it runs the migration again.
    public string? ConcurrentIndex { get; private init; }

    public bool RunsOutsideTransaction => ConcurrentIndex is not null;

    // A migration of exactly one create index concurrently statement that builds the named
    // index, for a table that is too large to block while an index builds.
    public static DatabaseMigration CreateIndexConcurrently(string id, string indexName, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!IndexNamePattern().IsMatch(indexName))
        {
            throw new ArgumentException(
                $"The index name '{indexName}' must be a plain lower-case identifier.",
                nameof(indexName));
        }

        var statement = sql.Trim().TrimEnd(';');
        if (statement.Contains(';', StringComparison.Ordinal)
            || !ConcurrentIndexPattern(indexName).IsMatch(statement))
        {
            throw new ArgumentException(
                $"Migration {id} must be one create index concurrently statement that builds {indexName}.",
                nameof(sql));
        }

        return new DatabaseMigration(id, sql) { ConcurrentIndex = indexName };
    }

    private static Regex ConcurrentIndexPattern(string indexName)
    {
        return new Regex(
            @"^create\s+(unique\s+)?index\s+concurrently\s+(if\s+not\s+exists\s+)?" + indexName + @"\s+on\s",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IndexNamePattern();
}
