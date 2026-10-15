using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Items;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// An item transaction service on its own traced data source. Scenario setup runs on the
// test context's untraced service, so a trace holds exactly one traced call.
internal sealed partial class TracedItemTransactions : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly GoldenFiles Goldens = GoldenFiles.ForProject(
        typeof(TracedItemTransactions).Assembly,
        "Golden",
        "ItemTransactions");

    private readonly PostgresIntegrationTestContext context;
    private readonly ILoggerFactory loggerFactory;
    private readonly NpgsqlDataSource dataSource;

    private TracedItemTransactions(
        PostgresIntegrationTestContext context,
        ItemOperationsOptions? options)
    {
        this.context = context;
        Recorder = new SqlTraceRecorder();
        loggerFactory = SqlTraceRecorder.CreateLoggerFactory(Recorder);
        dataSource = new NpgsqlDataSourceBuilder(Environment.GetEnvironmentVariable(
                PostgresIntegrationFactAttribute.ConnectionStringVariable))
            .UseLoggerFactory(loggerFactory)
            .Build();
        Service = options is null
            ? ActivatorUtilities.CreateInstance<ItemTransactionService>(context.Services, dataSource)
            : ActivatorUtilities.CreateInstance<ItemTransactionService>(
                context.Services,
                dataSource,
                options);
    }

    public SqlTraceRecorder Recorder { get; }

    public ItemTransactionService Service { get; }

    public static TracedItemTransactions Create(
        PostgresIntegrationTestContext context,
        ItemOperationsOptions? options = null)
    {
        return new TracedItemTransactions(context, options);
    }

    // Runs one traced call and compares its trace and journal with the golden file.
    public async Task<ItemTransactionResult> AssertGoldenAsync(
        string scenario,
        Guid operationId,
        Func<ItemTransactionService, Task<ItemTransactionResult>> execute)
    {
        Recorder.Start();
        ItemTransactionResult result;
        IReadOnlyList<string> trace;
        try
        {
            result = await execute(Service);
        }
        finally
        {
            trace = Recorder.Stop();
        }

        var rendered = await RenderAsync(scenario, operationId, trace, result);
        Goldens.AssertMatches(scenario, rendered);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync();
        loggerFactory.Dispose();
    }

    private async Task<string> RenderAsync(
        string scenario,
        Guid operationId,
        IReadOnlyList<string> trace,
        ItemTransactionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("scenario: ").Append(scenario).Append('\n');
        builder.Append("trace:\n");
        foreach (var line in trace)
        {
            builder.Append("  ").Append(line).Append('\n');
        }

        builder.Append("result:\n");
        builder.Append("  operation kind: ").Append(result.OperationKind).Append('\n');
        builder.Append("  operation id matches: ")
            .Append(result.OperationId == operationId ? "yes" : "no")
            .Append('\n');
        builder.Append("  succeeded: ").Append(result.Succeeded ? "yes" : "no").Append('\n');
        builder.Append("  error: ")
            .Append(result.Error is null
                ? "none"
                : $"{result.Error.Code} | {GuidPattern().Replace(result.Error.Message, "<id>")}")
            .Append('\n');
        builder.Append("  character revisions:");
        AppendSorted(builder, result.CharacterRevisions.Select(revision =>
            $"revision={revision.Revision} carried={revision.CarriedWeight} capacity={revision.CarryCapacity}"));
        builder.Append("  container revisions:");
        AppendSorted(builder, result.ContainerRevisions.Select(revision => $"revision={revision.Revision}"));
        builder.Append("  item revisions:");
        AppendSorted(builder, result.ItemRevisions.Select(revision => $"revision={revision.Revision}"));
        builder.Append("  recovery deliveries: ").Append(result.RecoveryDeliveryIds.Count).Append('\n');
        builder.Append("  secure entitlement revision: ")
            .Append(result.SecureContainerEntitlementRevision?.ToString() ?? "none")
            .Append('\n');

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var stored = await connection.QuerySingleOrDefaultAsync<StoredOperationRow>(
            """
            select
                operation_kind as "OperationKind",
                status as "Status",
                result_payload::text as "ResultPayload"
            from item_operations
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId });
        builder.Append("journal:\n");
        if (stored is null)
        {
            builder.Append("  operation row: none\n");
            return builder.ToString();
        }

        builder.Append("  operation row: ")
            .Append(stored.OperationKind)
            .Append(' ')
            .Append(stored.Status)
            .Append('\n');
        builder.Append("  stored payload: ")
            .Append(DescribeStoredPayload(stored.ResultPayload, result))
            .Append('\n');
        var changes = (await connection.QueryAsync<StoredChangeRow>(
            """
            select
                change_index as "ChangeIndex",
                change_kind as "ChangeKind",
                item_instance_id is null as "HasNoItem",
                before_state is null as "HasNoBefore",
                after_state is null as "HasNoAfter"
            from item_operation_changes
            where operation_id = @OperationId
            order by change_index;
            """,
            new { OperationId = operationId })).ToArray();
        builder.Append("  changes:");
        if (changes.Length == 0)
        {
            builder.Append(" none\n");
        }
        else
        {
            builder.Append('\n');
            foreach (var change in changes)
            {
                builder.Append("    ")
                    .Append(change.ChangeIndex)
                    .Append(' ')
                    .Append(change.ChangeKind)
                    .Append(change.HasNoItem ? " item=none" : " item=set")
                    .Append(change.HasNoBefore ? " before=none" : " before=set")
                    .Append(change.HasNoAfter ? " after=none" : " after=set")
                    .Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string DescribeStoredPayload(string? payload, ItemTransactionResult result)
    {
        if (payload is null)
        {
            return "none";
        }

        var storedResult = JsonSerializer.Deserialize<ItemTransactionResult>(payload, ResultJsonOptions);
        var storedJson = JsonSerializer.Serialize(storedResult, ResultJsonOptions);
        var returnedJson = JsonSerializer.Serialize(result, ResultJsonOptions);
        return string.Equals(storedJson, returnedJson, StringComparison.Ordinal)
            ? "equals returned result"
            : "differs from returned result";
    }

    private static void AppendSorted(StringBuilder builder, IEnumerable<string> values)
    {
        var sorted = values.Order(StringComparer.Ordinal).ToArray();
        if (sorted.Length == 0)
        {
            builder.Append(" none\n");
            return;
        }

        builder.Append('\n');
        foreach (var value in sorted)
        {
            builder.Append("    ").Append(value).Append('\n');
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    private sealed class StoredOperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ResultPayload { get; set; }
    }

    private sealed class StoredChangeRow
    {
        public int ChangeIndex { get; set; }

        public string ChangeKind { get; set; } = string.Empty;

        public bool HasNoItem { get; set; }

        public bool HasNoBefore { get; set; }

        public bool HasNoAfter { get; set; }
    }
}
