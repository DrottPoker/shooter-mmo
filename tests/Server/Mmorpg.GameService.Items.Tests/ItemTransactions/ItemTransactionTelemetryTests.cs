using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.Tests.ItemTransactions.ItemScenarioWorld;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Pins the observable telemetry of an item transaction: the logger category, the log
// level, template, and properties of every outcome, the correlation id precedence, the
// lock-wait scopes, and the transaction metric tags.
[Collection(SerialTestCollection.Name)]
public sealed class ItemTransactionTelemetryTests
{
    private const string RejectedTemplate =
        "Item transaction {OperationKind} {OperationId} was rejected with {ErrorCode} after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.";
    private const string CommittedTemplate =
        "Item transaction {OperationKind} {OperationId} committed after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.";
    private const string FailedTemplate =
        "Item transaction {OperationKind} {OperationId} failed unexpectedly for correlation {CorrelationId}.";
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";
    private static readonly Guid DerivedOperationId = Guid.Parse("8f14e45f-ceea-567a-9a6f-b3f0a2c4d1e8");

    [Fact]
    public async Task ServiceLogsUnderItsOwnCategory()
    {
        await using var dataSource = NpgsqlDataSource.Create(UnreachableDatabase);
        var recorder = new LogCategoryRecorder();
        var services = new ServiceCollection();
        services.AddGameServiceForTests(dataSource);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(recorder));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ItemTransactionService>();

        var result = await service.ExecuteAsync(
            InvalidIdRequest(),
            CancellationToken.None);

        Assert.Equal(ItemTransactionErrorCodes.ItemOperationIdInvalid, result.Error!.Code);
        var entry = Assert.Single(recorder.Entries);
        Assert.Equal("Mmorpg.GameService.Items.ItemTransactionService", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public async Task UnexpectedRejectionLogsWarningWithStableTemplateAndProperties()
    {
        var logger = new StructuredLogger<ItemTransactionService>();
        using var metrics = new ItemOperationsMetrics();
        var accessor = new HttpContextAccessor { HttpContext = null };
        await using var dataSource = NpgsqlDataSource.Create(UnreachableDatabase);
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(
            services,
            metrics,
            ItemOperationsOptions.CreateDefaults(),
            logger,
            accessor);

        var result = await service.ExecuteAsync(InvalidIdRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Null(entry.EventId.Name);
        Assert.Null(entry.Exception);
        Assert.Equal(RejectedTemplate, entry.Template);
        Assert.Equal(
            ["OperationKind", "OperationId", "ErrorCode", "DurationMilliseconds", "CorrelationId", "{OriginalFormat}"],
            entry.Properties.Keys.ToArray());
        Assert.Equal(ItemOperationKinds.Grant, entry.Properties["OperationKind"]);
        Assert.Equal(DerivedOperationId, entry.Properties["OperationId"]);
        Assert.Equal(ItemTransactionErrorCodes.ItemOperationIdInvalid, entry.Properties["ErrorCode"]);
        Assert.IsType<double>(entry.Properties["DurationMilliseconds"]);
        Assert.Equal(DerivedOperationId.ToString("N"), entry.Properties["CorrelationId"]);
        var snapshot = metrics.Capture();
        Assert.Equal(1, snapshot.Transactions);
        Assert.Equal(1, snapshot.RejectedTransactions);
    }

    [Fact]
    public async Task CorrelationIdPrefersHeaderThenActivityThenTraceIdentifierThenOperationId()
    {
        Assert.Null(Activity.Current);
        var headerCorrelation = Guid.NewGuid();
        var headerContext = new DefaultHttpContext { TraceIdentifier = "trace-identifier-1" };
        headerContext.Request.Headers["X-Correlation-ID"] = headerCorrelation.ToString("N");
        using (new Activity("item-transaction-telemetry-test").Start())
        {
            Assert.Equal(headerCorrelation.ToString("N"), await LogCorrelationAsync(headerContext));
        }

        var invalidHeaderContext = new DefaultHttpContext { TraceIdentifier = "trace-identifier-2" };
        invalidHeaderContext.Request.Headers["X-Correlation-ID"] = Guid.NewGuid().ToString("D");
        using (var activity = new Activity("item-transaction-telemetry-test").Start())
        {
            Assert.Equal(activity.TraceId.ToString(), await LogCorrelationAsync(invalidHeaderContext));
        }

        Assert.Null(Activity.Current);
        Assert.Equal(
            "trace-identifier-3",
            await LogCorrelationAsync(new DefaultHttpContext { TraceIdentifier = "trace-identifier-3" }));
        Assert.Equal(DerivedOperationId.ToString("N"), await LogCorrelationAsync(null));
    }

    [Fact]
    public async Task UnexpectedFailureLogsErrorAndRethrows()
    {
        var logger = new StructuredLogger<ItemTransactionService>();
        using var metrics = new ItemOperationsMetrics();
        await using var dataSource = NpgsqlDataSource.Create(UnreachableDatabase);
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(
            services,
            metrics,
            ItemOperationsOptions.CreateDefaults(),
            logger);
        var operationId = Guid.NewGuid();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(
            new ItemTransactionRequest<ExpireCorpseCommand>(
                operationId,
                ItemTransactionActor.ForSystem(),
                new ExpireCorpseCommand(Guid.NewGuid())),
            CancellationToken.None));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(FailedTemplate, entry.Template);
        Assert.Equal(
            ["OperationKind", "OperationId", "CorrelationId", "{OriginalFormat}"],
            entry.Properties.Keys.ToArray());
        Assert.Equal(ItemOperationKinds.ExpireCorpse, entry.Properties["OperationKind"]);
        Assert.Equal(operationId, entry.Properties["OperationId"]);
        Assert.Equal(0, metrics.Capture().Transactions);
    }

    [PostgresIntegrationFact]
    public async Task OutcomesLogAtTheirLevelsAndRecordLockScopesAndTags()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("telemetry_player");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var logger = new StructuredLogger<ItemTransactionService>();
        using var metrics = new ItemOperationsMetrics();
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(
            context.Services,
            metrics,
            ItemOperationsOptions.CreateDefaults(),
            logger);
        var lockScopes = new List<string>();
        var transactionTags = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, ItemOperationsMetrics.MeterName, StringComparison.Ordinal)
                && instrument.Name is "game_service.items.locks.wait.duration" or "game_service.items.transactions")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            lock (lockScopes)
            {
                lockScopes.Add(Tag(tags, "scope"));
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (transactionTags)
            {
                transactionTags.Add($"{Tag(tags, "operation.kind")}/{Tag(tags, "outcome")}/{value}");
            }
        });
        listener.Start();

        var grant = await service.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    "ring.starter_band",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertCommitted(grant);
        Assert.Equal(["operation_claim", "character_state", "mutation_scope"], lockScopes);
        Assert.Equal(["grant/committed/1"], transactionTags);
        var committed = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, committed.Level);
        Assert.Equal(CommittedTemplate, committed.Template);
        Assert.Equal(
            ["OperationKind", "OperationId", "DurationMilliseconds", "CorrelationId", "{OriginalFormat}"],
            committed.Properties.Keys.ToArray());

        var ringId = Assert.Single(grant.ItemRevisions).ItemInstanceId;
        state = await world.LoadStateAsync(characterId);
        var ring = await world.LoadItemAsync(ringId);
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new ApplyItemPolicyCommand(
                    characterId,
                    state.Revision,
                    ringId,
                    ring.Revision,
                    ItemPolicyIds.ProtectedOnDeath,
                    "telemetry",
                    "telemetry")),
            CancellationToken.None));
        var significant = logger.Entries[^1];
        Assert.Equal(LogLevel.Information, significant.Level);
        Assert.Equal(CommittedTemplate, significant.Template);

        state = await world.LoadStateAsync(characterId);
        var stale = await service.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new RelocateItemCommand(
                    characterId,
                    state.Revision - 1,
                    ringId,
                    0,
                    state.PermanentInventoryContainerId,
                    1)),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, stale.Error!.Code);
        var contention = logger.Entries[^1];
        Assert.Equal(LogLevel.Debug, contention.Level);
        Assert.Equal(RejectedTemplate, contention.Template);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, contention.Properties["ErrorCode"]);
        Assert.Equal(
            ["grant/committed/1", "apply_item_policy/committed/1", "relocate/rejected/1"],
            transactionTags);
    }

    private static async Task<object?> LogCorrelationAsync(HttpContext? httpContext)
    {
        var logger = new StructuredLogger<ItemTransactionService>();
        await using var dataSource = NpgsqlDataSource.Create(UnreachableDatabase);
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(
            services,
            ItemOperationsOptions.CreateDefaults(),
            logger,
            accessor);

        await service.ExecuteAsync(InvalidIdRequest(), CancellationToken.None);
        return Assert.Single(logger.Entries).Properties["CorrelationId"];
    }

    private static ItemTransactionRequest<GrantItemCommand> InvalidIdRequest()
    {
        return new ItemTransactionRequest<GrantItemCommand>(
            DerivedOperationId,
            ItemTransactionActor.ForAccount(Guid.NewGuid()),
            new GrantItemCommand(Guid.NewGuid(), 1, "material.iron_ore", 1, Guid.NewGuid(), 0));
    }

    private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return Convert.ToString(tag.Value) ?? "null";
            }
        }

        return "missing";
    }
}
