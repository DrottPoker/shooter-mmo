using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// A transaction that throws is still counted: the transaction counter and duration
// record outcome "failed", or "cancelled" when the caller cancelled the request.
[Collection(SerialTestCollection.Name)]
public sealed class ItemTransactionFailureMetricsTests
{
    [Fact]
    public async Task UnexpectedFailuresAndCancellationsAreCountedWithTheirOutcome()
    {
        using var metrics = new ItemOperationsMetrics();
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var service = ActivatorUtilities.CreateInstance<ItemTransactionService>(
            services,
            metrics,
            ItemOperationsOptions.CreateDefaults());
        var counted = new List<string>();
        var timed = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, ItemOperationsMetrics.MeterName, StringComparison.Ordinal)
                && instrument.Name is "auth_service.items.transactions" or "auth_service.items.transactions.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (counted)
            {
                counted.Add($"{Tag(tags, "operation.kind")}/{Tag(tags, "outcome")}/{value}");
            }
        });
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            lock (timed)
            {
                timed.Add($"{Tag(tags, "operation.kind")}/{Tag(tags, "outcome")}");
            }
        });
        listener.Start();

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => service.ExecuteAsync(
            ExpiryRequest(),
            CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(
            ExpiryRequest(),
            cancellation.Token));

        Assert.Equal(["expire_corpse/failed/1", "expire_corpse/cancelled/1"], counted);
        Assert.Equal(["expire_corpse/failed", "expire_corpse/cancelled"], timed);
        // The snapshot totals stay committed plus rejected transactions.
        Assert.Equal(0, metrics.Capture().Transactions);
    }

    private static ItemTransactionRequest<ExpireCorpseCommand> ExpiryRequest()
    {
        return new ItemTransactionRequest<ExpireCorpseCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            new ExpireCorpseCommand(Guid.NewGuid()));
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
