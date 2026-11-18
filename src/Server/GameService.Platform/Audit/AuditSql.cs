using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Platform.Database;

namespace Mmorpg.GameService.Platform.Audit;

// The one statement shape of an audit check: it counts what the check looked at, counts the rows
// that break the check, and returns the first of them as examples, all from the run's snapshot.
public static class AuditSql
{
    public const string ExampleLimitParameter = "AuditExampleLimit";

    // The client waits a little longer than the server, so a statement timeout ends a check as a
    // PostgreSQL error that the run survives instead of as a broken connection.
    private static readonly TimeSpan ClientTimeoutMargin = TimeSpan.FromSeconds(10);

    // A statement over the check's own SQL. examined is a scalar select, violations a select whose
    // columns become the fields of an example, and orderBy names its columns that order the
    // examples. None of them ends with a semicolon.
    public static string Counted(string examined, string violations, string orderBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examined);
        ArgumentException.ThrowIfNullOrWhiteSpace(violations);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderBy);
        return $"""
            with violation as materialized (
            {violations}
            )
            select
                ({examined})::bigint as "Examined",
                (select count(*) from violation)::bigint as "Violations",
                coalesce((
                    select json_agg(row_to_json(example))
                    from (
                        select *
                        from violation
                        order by {orderBy}
                        limit @{ExampleLimitParameter}
                    ) example), '[]'::json)::text as "Examples";
            """;
    }

    // Runs a statement that Counted built, with the check's parameters and the example limit.
    public static async Task<AuditCheckResult> RunAsync(
        PostgresTransactionScope scope,
        AuditCheckContext context,
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        var arguments = new DynamicParameters(parameters);
        arguments.Add(ExampleLimitParameter, context.ExampleLimit);
        var row = await scope.Connection.QuerySingleAsync<CountedRow>(new CommandDefinition(
            sql,
            arguments,
            scope.Transaction,
            commandTimeout: ClientTimeoutSeconds(context.Options.StatementTimeout),
            cancellationToken: cancellationToken));
        return new AuditCheckResult(row.Examined, row.Violations, ParseExamples(row.Examples));
    }

    internal static int ClientTimeoutSeconds(TimeSpan statementTimeout)
    {
        return (int)Math.Ceiling((statementTimeout + ClientTimeoutMargin).TotalSeconds);
    }

    // Every field keeps its column's place and its value as text: a string as it is, a number or
    // a boolean as its JSON text, and null as null.
    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> ParseExamples(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var examples = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var example = new OrderedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                example[field.Name] = field.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => field.Value.GetString(),
                    _ => field.Value.GetRawText()
                };
            }

            examples.Add(example);
        }

        return examples;
    }

    private sealed class CountedRow
    {
        public long Examined { get; set; }

        public long Violations { get; set; }

        public string? Examples { get; set; }
    }
}
