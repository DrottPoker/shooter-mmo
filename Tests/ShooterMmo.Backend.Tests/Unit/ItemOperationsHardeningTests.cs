using System.Diagnostics.Metrics;
using AuthService.Items;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ItemOperationsHardeningTests
{
    [Fact]
    public void ConfigurationBoundsTimeoutsPayloadsAndRetentionOrder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Items:Operations:TransactionTimeoutMilliseconds"] = "5000",
                ["Items:Operations:LockTimeoutMilliseconds"] = "250",
                ["Items:Operations:MaximumCommandPayloadBytes"] = "8192",
                ["Items:Operations:MaximumHttpRequestBodyBytes"] = "32768",
                ["Items:Operations:MetricsIntervalSeconds"] = "10",
                ["Items:Operations:MaintenanceIntervalSeconds"] = "20",
                ["Items:Operations:CleanupBatchSize"] = "32",
                ["Items:Operations:AuditRetentionDays"] = "14",
                ["Items:Operations:ClosedCorpseRetentionDays"] = "21"
            })
            .Build();

        var options = ItemOperationsOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(5), options.TransactionTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.LockTimeout);
        Assert.Equal(8192, options.MaximumCommandPayloadBytes);
        Assert.Equal(32768, options.MaximumHttpRequestBodyBytes);
        Assert.Equal(32, options.CleanupBatchSize);
        Assert.Equal(TimeSpan.FromDays(14), options.AuditRetention);
        Assert.Equal(TimeSpan.FromDays(21), options.ClosedCorpseRetention);
    }

    [Theory]
    [InlineData("Items:Operations:LockTimeoutMilliseconds", "11000")]
    [InlineData("Items:Operations:MaximumCommandPayloadBytes", "10")]
    [InlineData("Items:Operations:AuditRetentionDays", "31")]
    public void InvalidOperationalBoundsFailFast(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["Items:Operations:TransactionTimeoutMilliseconds"] = "10000",
            ["Items:Operations:LockTimeoutMilliseconds"] = "2000",
            ["Items:Operations:AuditRetentionDays"] = "30",
            ["Items:Operations:ClosedCorpseRetentionDays"] = "30",
            [key] = value
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => ItemOperationsOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void MetricsUseOnlyBoundedDimensionsAndTrackOperationalOutcomes()
    {
        using var metrics = new ItemOperationsMetrics();
        using var listener = new MeterListener();
        var observedTagKeys = new HashSet<string>(StringComparer.Ordinal);
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ItemOperationsMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                observedTagKeys.Add(tag.Key);
            }
        });
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                observedTagKeys.Add(tag.Key);
            }
        });
        listener.Start();

        metrics.RecordTransaction(
            ItemOperationKinds.Grant,
            Success(ItemOperationKinds.Grant),
            TimeSpan.FromMilliseconds(4));
        metrics.RecordTransaction(
            ItemOperationKinds.Relocate,
            Rejected(
                ItemOperationKinds.Relocate,
                ItemTransactionErrorCodes.ItemStateConflict),
            TimeSpan.FromMilliseconds(6));
        metrics.RecordTransaction(
            ItemOperationKinds.ClaimRecoveryDelivery,
            Rejected(
                ItemOperationKinds.ClaimRecoveryDelivery,
                ItemTransactionErrorCodes.ItemTransactionTimeout),
            TimeSpan.FromMilliseconds(2000));
        metrics.RecordLockWait("character_state", TimeSpan.FromMilliseconds(2));
        metrics.ObserveOperationalState(3, 4, 5);
        metrics.RecordCleanup("recovery_delivery", 2);
        metrics.RecordCleanup("closed_corpse", 1);
        metrics.RecordCleanup("audit_operation", 7);
        listener.RecordObservableInstruments();

        var snapshot = metrics.Capture();
        Assert.Equal(3, snapshot.Transactions);
        Assert.Equal(1, snapshot.CommittedTransactions);
        Assert.Equal(2, snapshot.RejectedTransactions);
        Assert.Equal(2, snapshot.Conflicts);
        Assert.Equal(1, snapshot.StaleRevisions);
        Assert.Equal(1, snapshot.Timeouts);
        Assert.Equal(1, snapshot.LockWaitSamples);
        Assert.Equal(3, snapshot.OpenCorpses);
        Assert.Equal(4, snapshot.ClosedCorpses);
        Assert.Equal(5, snapshot.RecoveryBacklog);
        Assert.Equal(2, snapshot.ExpiredRecoveryDeliveries);
        Assert.Equal(1, snapshot.RemovedClosedCorpses);
        Assert.Equal(7, snapshot.RemovedAuditOperations);
        Assert.Subset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "operation.kind",
                "outcome",
                "kind",
                "scope",
                "state",
                "target",
                "action",
                "persistence"
            },
            observedTagKeys);
        Assert.DoesNotContain(
            observedTagKeys,
            key => key.Contains("id", StringComparison.OrdinalIgnoreCase)
                || key.Contains("account", StringComparison.OrdinalIgnoreCase)
                || key.Contains("character", StringComparison.OrdinalIgnoreCase)
                || key.Contains("session", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OversizedCanonicalCommandIsRejectedBeforeDatabaseAccess()
    {
        var options = ItemOperationsOptions.CreateDefaults() with
        {
            MaximumCommandPayloadBytes = 1024
        };
        using var metrics = new ItemOperationsMetrics();
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var service = new ItemTransactionService(dataSource, metrics, options);
        var result = await service.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    Guid.NewGuid(),
                    null,
                    "phase15_load",
                    new string('x', 2048),
                    null,
                    null,
                    [])),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            ItemTransactionErrorCodes.ItemCommandPayloadTooLarge,
            result.Error!.Code);
        Assert.Equal(1, metrics.Capture().RejectedTransactions);
    }

    [Fact]
    public void CorpseMetricsSeparateLiveAndDurableWithoutIdentityLabels()
    {
        using var metrics = new SimulationWorker.Corpses.CorpseRuntimeMetrics();

        metrics.Observe(7, 19);

        var snapshot = metrics.Capture();
        Assert.Equal(7, snapshot.DurableCorpses);
        Assert.Equal(19, snapshot.LiveCorpses);
    }

    private static ItemTransactionResult Success(string operationKind)
    {
        return new ItemTransactionResult(
            Guid.NewGuid(),
            operationKind,
            true,
            null,
            [],
            [],
            [],
            [],
            null);
    }

    private static ItemTransactionResult Rejected(string operationKind, string errorCode)
    {
        return new ItemTransactionResult(
            Guid.NewGuid(),
            operationKind,
            false,
            new ItemTransactionError(errorCode, "Rejected for test."),
            [],
            [],
            [],
            [],
            null);
    }
}
