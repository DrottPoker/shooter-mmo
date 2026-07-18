using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AuthService.Items;

public sealed record ItemOperationsMetricsSnapshot(
    long Transactions,
    long CommittedTransactions,
    long RejectedTransactions,
    long Conflicts,
    long StaleRevisions,
    long Timeouts,
    long DeathPartitions,
    long PolicyActions,
    long LockWaitSamples,
    long OpenCorpses,
    long ClosedCorpses,
    long RecoveryBacklog,
    long ExpiredRecoveryDeliveries,
    long RemovedClosedCorpses,
    long RemovedAuditOperations);

public sealed class ItemOperationsMetrics : IDisposable
{
    public const string MeterName = "ShooterMmo.AuthService.Items";

    private static readonly HashSet<string> PolicyOperationKinds = new(StringComparer.Ordinal)
    {
        ItemOperationKinds.ApplyItemPolicy,
        ItemOperationKinds.RemoveInsurancePolicy,
        ItemOperationKinds.AbandonQuestItems,
        ItemOperationKinds.ProcessPlayerDeath
    };

    private readonly Meter meter = new(MeterName);
    private readonly Histogram<double> transactionLatency;
    private readonly Histogram<double> lockWaitLatency;
    private readonly Counter<long> transactionCounter;
    private readonly Counter<long> conflictCounter;
    private readonly Counter<long> deathPartitionCounter;
    private readonly Counter<long> policyActionCounter;
    private readonly Counter<long> cleanupCounter;
    private long transactions;
    private long committedTransactions;
    private long rejectedTransactions;
    private long conflicts;
    private long staleRevisions;
    private long timeouts;
    private long deathPartitions;
    private long policyActions;
    private long lockWaitSamples;
    private long openCorpses;
    private long closedCorpses;
    private long recoveryBacklog;
    private long expiredRecoveryDeliveries;
    private long removedClosedCorpses;
    private long removedAuditOperations;

    public ItemOperationsMetrics()
    {
        transactionLatency = meter.CreateHistogram<double>(
            "auth_service.items.transactions.duration",
            "ms");
        lockWaitLatency = meter.CreateHistogram<double>(
            "auth_service.items.locks.wait.duration",
            "ms");
        transactionCounter = meter.CreateCounter<long>(
            "auth_service.items.transactions");
        conflictCounter = meter.CreateCounter<long>(
            "auth_service.items.conflicts");
        deathPartitionCounter = meter.CreateCounter<long>(
            "auth_service.items.death_partitions");
        policyActionCounter = meter.CreateCounter<long>(
            "auth_service.items.policy_actions");
        cleanupCounter = meter.CreateCounter<long>(
            "auth_service.items.cleanup");
        meter.CreateObservableGauge(
            "auth_service.items.corpses",
            ObserveCorpseCounts);
        meter.CreateObservableGauge(
            "auth_service.items.recovery.backlog",
            () => Volatile.Read(ref recoveryBacklog));
    }

    public void RecordTransaction(
        string operationKind,
        ItemTransactionResult result,
        TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentNullException.ThrowIfNull(result);
        var metricOperationKind = KnownOperationKinds.Contains(operationKind)
            ? operationKind
            : "unknown";
        var outcome = result.Succeeded ? "committed" : "rejected";
        var tags = new TagList
        {
            { "operation.kind", metricOperationKind },
            { "outcome", outcome }
        };
        transactionLatency.Record(Math.Max(0d, duration.TotalMilliseconds), tags);
        transactionCounter.Add(1, tags);
        Interlocked.Increment(ref transactions);
        if (result.Succeeded)
        {
            Interlocked.Increment(ref committedTransactions);
        }
        else
        {
            Interlocked.Increment(ref rejectedTransactions);
            RecordConflict(result.Error?.Code);
        }

        if (result.Succeeded
            && string.Equals(
                operationKind,
                ItemOperationKinds.ProcessPlayerDeath,
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref deathPartitions);
            deathPartitionCounter.Add(1);
        }

        if (PolicyOperationKinds.Contains(operationKind))
        {
            Interlocked.Increment(ref policyActions);
            var policyTags = new TagList
            {
                { "action", operationKind },
                { "outcome", outcome }
            };
            policyActionCounter.Add(1, policyTags);
        }
    }

    public void RecordLockWait(string scope, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var metricScope = scope is "operation_claim"
            or "character_state"
            or "character_event"
            or "mutation_scope"
            ? scope
            : "other";
        lockWaitLatency.Record(
            Math.Max(0d, duration.TotalMilliseconds),
            new KeyValuePair<string, object?>("scope", metricScope));
        Interlocked.Increment(ref lockWaitSamples);
    }

    public void ObserveOperationalState(
        long currentOpenCorpses,
        long currentClosedCorpses,
        long currentRecoveryBacklog)
    {
        if (currentOpenCorpses < 0 || currentClosedCorpses < 0 || currentRecoveryBacklog < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentOpenCorpses),
                "Operational item counts cannot be negative.");
        }

        Interlocked.Exchange(ref openCorpses, currentOpenCorpses);
        Interlocked.Exchange(ref closedCorpses, currentClosedCorpses);
        Interlocked.Exchange(ref recoveryBacklog, currentRecoveryBacklog);
    }

    public void RecordCleanup(string target, long count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (count <= 0)
        {
            return;
        }

        switch (target)
        {
            case "recovery_delivery":
                Interlocked.Add(ref expiredRecoveryDeliveries, count);
                break;
            case "closed_corpse":
                Interlocked.Add(ref removedClosedCorpses, count);
                break;
            case "audit_operation":
                Interlocked.Add(ref removedAuditOperations, count);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    target,
                    "The cleanup target is not a bounded item operations metric dimension.");
        }

        cleanupCounter.Add(
            count,
            new KeyValuePair<string, object?>("target", target));
    }

    public ItemOperationsMetricsSnapshot Capture()
    {
        return new ItemOperationsMetricsSnapshot(
            Volatile.Read(ref transactions),
            Volatile.Read(ref committedTransactions),
            Volatile.Read(ref rejectedTransactions),
            Volatile.Read(ref conflicts),
            Volatile.Read(ref staleRevisions),
            Volatile.Read(ref timeouts),
            Volatile.Read(ref deathPartitions),
            Volatile.Read(ref policyActions),
            Volatile.Read(ref lockWaitSamples),
            Volatile.Read(ref openCorpses),
            Volatile.Read(ref closedCorpses),
            Volatile.Read(ref recoveryBacklog),
            Volatile.Read(ref expiredRecoveryDeliveries),
            Volatile.Read(ref removedClosedCorpses),
            Volatile.Read(ref removedAuditOperations));
    }

    public void Dispose()
    {
        meter.Dispose();
    }

    private IEnumerable<Measurement<long>> ObserveCorpseCounts()
    {
        yield return new Measurement<long>(
            Volatile.Read(ref openCorpses),
            new KeyValuePair<string, object?>("state", "open"));
        yield return new Measurement<long>(
            Volatile.Read(ref closedCorpses),
            new KeyValuePair<string, object?>("state", "closed"));
    }

    private void RecordConflict(string? errorCode)
    {
        var kind = errorCode switch
        {
            ItemTransactionErrorCodes.ItemOperationConflict => "operation",
            ItemTransactionErrorCodes.ItemStateConflict
                or ItemTransactionErrorCodes.ItemQuantityChanged
                or ItemTransactionErrorCodes.BagStateChanged
                or ItemTransactionErrorCodes.CorpseStateChanged => "stale_revision",
            ItemTransactionErrorCodes.ItemTransactionTimeout => "timeout",
            _ => null
        };
        if (kind is null)
        {
            return;
        }

        Interlocked.Increment(ref conflicts);
        if (kind == "stale_revision")
        {
            Interlocked.Increment(ref staleRevisions);
        }
        else if (kind == "timeout")
        {
            Interlocked.Increment(ref timeouts);
        }

        conflictCounter.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind));
    }

    private static readonly HashSet<string> KnownOperationKinds = new(StringComparer.Ordinal)
    {
        ItemOperationKinds.Grant,
        ItemOperationKinds.Relocate,
        ItemOperationKinds.Equip,
        ItemOperationKinds.Unequip,
        ItemOperationKinds.SplitStack,
        ItemOperationKinds.MergeStacks,
        ItemOperationKinds.SwapContainerItems,
        ItemOperationKinds.ConsumeQuantity,
        ItemOperationKinds.Destroy,
        ItemOperationKinds.SwapBagAggregates,
        ItemOperationKinds.AddRecoveryDelivery,
        ItemOperationKinds.ClaimRecoveryDelivery,
        ItemOperationKinds.ChangeSecureContainerTier,
        ItemOperationKinds.ApplyItemPolicy,
        ItemOperationKinds.RemoveInsurancePolicy,
        ItemOperationKinds.AbandonQuestItems,
        ItemOperationKinds.ProcessPlayerDeath,
        ItemOperationKinds.CreatePersistentMobCorpse,
        ItemOperationKinds.GrantMobLoot,
        ItemOperationKinds.ExpireCorpse,
        ItemOperationKinds.ExpireRecoveryDelivery,
        ItemOperationKinds.LootCorpseItem,
        ItemOperationKinds.LootCorpsePartialStack,
        ItemOperationKinds.DepositCorpseItem,
        ItemOperationKinds.DepositCorpsePartialStack,
        ItemOperationKinds.MoveCorpseItem,
        ItemOperationKinds.MoveCorpsePartialStack,
        ItemOperationKinds.SwapCorpseBag
    };
}
