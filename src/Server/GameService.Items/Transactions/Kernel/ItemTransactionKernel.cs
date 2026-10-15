using System.Data;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// Owns the lifetime of one item transaction: admission, BEGIN, the transaction-local
// timeouts, live authority, the idempotent claim, the mutation savepoint, the handler,
// finalization, completion, and commit or rollback. Handlers never end a transaction.
internal sealed class ItemTransactionKernel(
    NpgsqlDataSource dataSource,
    ItemOperationsOptions operationsOptions,
    ItemOperationsMetrics? operationsMetrics,
    ItemTransactionTelemetry telemetry,
    ItemOperationLedger ledger,
    ItemSimulationAuthority simulationAuthority,
    ItemTransactionDependencies dependencies)
{
    private const string MutationSavepoint = "item_mutation";

    public async Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        string operationKind,
        Guid? actorCharacterId,
        IItemCommandHandler<TCommand> handler,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var correlationId = telemetry.ResolveCorrelationId(request.OperationId);
        try
        {
            var result = await ExecuteCoreAsync(
                request,
                operationKind,
                actorCharacterId,
                handler,
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(started);
            telemetry.RecordCompleted(
                operationKind,
                request.OperationId,
                result,
                duration,
                correlationId);
            return result;
        }
        catch (Exception exception)
        {
            telemetry.RecordFailed(
                exception,
                operationKind,
                request.OperationId,
                correlationId);
            throw;
        }
    }

    private async Task<ItemTransactionResult> ExecuteCoreAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        string operationKind,
        Guid? actorCharacterId,
        IItemCommandHandler<TCommand> handler,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Actor);
        ArgumentNullException.ThrowIfNull(request.Command);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("An item operation id is required.", nameof(request));
        }

        if (ItemOperationAdmission.RequiresRandomClientOperationId(request.Actor, operationKind)
            && !ItemOperationIds.IsRandomClientOperationId(request.OperationId))
        {
            return ItemTransactionOutcomes.CreateRejected(
                request.OperationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationIdInvalid,
                "The item operation id must be a random version 4 UUID.");
        }

        var canonicalRequest = ItemOperationRequestCanonicalizer.Canonicalize(
            request.Actor,
            request.Command);
        var requestPayload = canonicalRequest.Payload;
        var requestHash = canonicalRequest.Hash;
        if (ItemOperationAdmission.ExceedsPayloadLimit(
                requestPayload,
                operationsOptions))
        {
            return ItemTransactionOutcomes.CreateRejected(
                request.OperationId,
                operationKind,
                ItemTransactionErrorCodes.ItemCommandPayloadTooLarge,
                "The item operation command payload exceeds the configured size limit.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select
                    set_config('statement_timeout', @StatementTimeout, true),
                    set_config('lock_timeout', @LockTimeout, true);
                """,
                new
                {
                    StatementTimeout = $"{operationsOptions.TransactionTimeout.TotalMilliseconds:0}ms",
                    LockTimeout = $"{operationsOptions.LockTimeout.TotalMilliseconds:0}ms"
                },
                transaction,
                cancellationToken: cancellationToken));
            var session = new ItemTransactionSession(
                connection,
                transaction,
                request.OperationId,
                operationKind,
                request.Actor);
            var context = new ItemTransactionContext(
                session,
                dependencies,
                operationsMetrics);
            try
            {
                ItemActorAuthority.EnsureValid(session.Actor);
                await simulationAuthority.ValidateLiveSessionAsync(
                    session,
                    actorCharacterId,
                    cancellationToken);
            }
            catch (ItemTransactionRejectedException exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                // A lost response can make the worker retry after its session ended. A
                // committed result must still replay instead of turning into a failure.
                var committedReplay = await ledger.LoadCommittedReplayAsync(
                    request.OperationId,
                    operationKind,
                    requestHash,
                    cancellationToken);
                return committedReplay ?? ItemTransactionOutcomes.CreateRejected(
                    request.OperationId,
                    operationKind,
                    exception.Code,
                    exception.Message);
            }

            var claimStarted = Stopwatch.GetTimestamp();
            ItemTransactionResult? replay;
            try
            {
                replay = await ledger.ClaimAsync(
                    connection,
                    transaction,
                    request.OperationId,
                    request.Actor,
                    actorCharacterId,
                    operationKind,
                    requestHash,
                    requestPayload,
                    cancellationToken);
            }
            finally
            {
                operationsMetrics?.RecordLockWait(
                    "operation_claim",
                    Stopwatch.GetElapsedTime(claimStarted));
            }
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"savepoint {MutationSavepoint};",
                transaction: transaction,
                cancellationToken: cancellationToken));

            try
            {
                await handler.ExecuteAsync(context, request.Command, cancellationToken);
                var result = await new ItemTransactionFinalizer(session, context.Journal)
                    .FinalizeAsync(cancellationToken);
                await ledger.CompleteAsync(
                    connection,
                    transaction,
                    result,
                    "committed",
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (ItemTransactionRejectedException exception)
            {
                return await RejectMutationAsync(
                    connection,
                    transaction,
                    request.OperationId,
                    operationKind,
                    exception.Code,
                    exception.Message,
                    cancellationToken);
            }
            catch (PostgresException exception)
            {
                var mapped = ItemTransactionOutcomes.MapPostgresError(exception);
                if (mapped is null)
                {
                    throw;
                }

                return await RejectMutationAsync(
                    connection,
                    transaction,
                    request.OperationId,
                    operationKind,
                    mapped.Code,
                    mapped.Message,
                    cancellationToken);
            }
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            var mapped = ItemTransactionOutcomes.MapPostgresError(exception);
            if (mapped is null)
            {
                throw;
            }

            return ItemTransactionOutcomes.CreateRejected(
                request.OperationId,
                operationKind,
                mapped.Code,
                mapped.Message);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ItemTransactionResult> RejectMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // A transient database rejection is not an outcome of the request, and a retried
        // system operation must stay claimable, so both release the operation claim.
        if (errorCode == ItemTransactionErrorCodes.ItemTransactionTimeout
            || ItemOperationKinds.IsRetriedSystemOperation(operationKind))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ItemTransactionOutcomes.CreateRejected(
                operationId,
                operationKind,
                errorCode,
                errorMessage);
        }

        return await RollBackMutationAndRejectAsync(
            connection,
            transaction,
            operationId,
            operationKind,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    private async Task<ItemTransactionResult> RollBackMutationAndRejectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"rollback to savepoint {MutationSavepoint};",
            transaction: transaction,
            cancellationToken: cancellationToken));
        var result = ItemTransactionOutcomes.CreateRejected(
            operationId,
            operationKind,
            errorCode,
            errorMessage);
        await ledger.CompleteAsync(
            connection,
            transaction,
            result,
            "rejected",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
