using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mmorpg.GameService.Items.Transactions;

// Metrics and logs for one item transaction. The logger keeps the ItemTransactionService
// category, and metrics are recorded before the log entry.
internal sealed class ItemTransactionTelemetry(
    ItemOperationsMetrics? operationsMetrics,
    ILogger<ItemTransactionService>? logger,
    IHttpContextAccessor? httpContextAccessor)
{
    public string ResolveCorrelationId(Guid operationId)
    {
        var header = httpContextAccessor?.HttpContext?
            .Request.Headers["X-Correlation-ID"]
            .FirstOrDefault();
        if (Guid.TryParseExact(header, "N", out var correlationId))
        {
            return correlationId.ToString("N");
        }

        return Activity.Current?.TraceId.ToString()
            ?? httpContextAccessor?.HttpContext?.TraceIdentifier
            ?? operationId.ToString("N");
    }

    public void RecordCompleted(
        string operationKind,
        Guid operationId,
        ItemTransactionResult result,
        TimeSpan duration,
        string correlationId)
    {
        operationsMetrics?.RecordTransaction(operationKind, result, duration);
        if (logger is not null)
        {
            if (!result.Succeeded && IsExpectedContention(result.Error?.Code))
            {
                logger.LogDebug(
                    "Item transaction {OperationKind} {OperationId} was rejected with {ErrorCode} after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.",
                    operationKind,
                    operationId,
                    result.Error?.Code ?? "item_transaction_rejected",
                    duration.TotalMilliseconds,
                    correlationId);
            }
            else if (!result.Succeeded)
            {
                logger.LogWarning(
                    "Item transaction {OperationKind} {OperationId} was rejected with {ErrorCode} after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.",
                    operationKind,
                    operationId,
                    result.Error?.Code ?? "item_transaction_rejected",
                    duration.TotalMilliseconds,
                    correlationId);
            }
            else if (IsOperationallySignificant(operationKind))
            {
                logger.LogInformation(
                    "Item transaction {OperationKind} {OperationId} committed after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.",
                    operationKind,
                    operationId,
                    duration.TotalMilliseconds,
                    correlationId);
            }
            else
            {
                logger.LogDebug(
                    "Item transaction {OperationKind} {OperationId} committed after {DurationMilliseconds:F3} ms for correlation {CorrelationId}.",
                    operationKind,
                    operationId,
                    duration.TotalMilliseconds,
                    correlationId);
            }
        }
    }

    public void RecordFailed(
        Exception exception,
        string operationKind,
        Guid operationId,
        string correlationId)
    {
        logger?.LogError(
            exception,
            "Item transaction {OperationKind} {OperationId} failed unexpectedly for correlation {CorrelationId}.",
            operationKind,
            operationId,
            correlationId);
    }

    private static bool IsOperationallySignificant(string operationKind)
    {
        return operationKind is ItemOperationKinds.ProcessPlayerDeath
            or ItemOperationKinds.CreatePersistentMobCorpse
            or ItemOperationKinds.ExpireCorpse
            or ItemOperationKinds.ApplyItemPolicy
            or ItemOperationKinds.RemoveInsurancePolicy
            or ItemOperationKinds.AbandonQuestItems;
    }

    private static bool IsExpectedContention(string? errorCode)
    {
        return errorCode is ItemTransactionErrorCodes.ItemStateConflict
            or ItemTransactionErrorCodes.ItemOperationConflict
            or ItemTransactionErrorCodes.ItemSlotOccupied
            or ItemTransactionErrorCodes.EquipmentSlotOccupied
            or ItemTransactionErrorCodes.ItemQuantityChanged
            or ItemTransactionErrorCodes.BagStateChanged
            or ItemTransactionErrorCodes.CorpseStateChanged
            or ItemTransactionErrorCodes.ItemAlreadyLooted;
    }
}
