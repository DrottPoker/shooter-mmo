using System.Data;
using System.Diagnostics;
using Dapper;
using Mmorpg.GameService.Items.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// Owns the lifetime of one item transaction: admission, BEGIN, the transaction-local
// timeouts, live authority, the idempotent claim, the mutation savepoint, the handler,
// finalization, completion, and commit or rollback. Handlers never end a transaction.
// The command's registration supplies its operation kind, actor character, handler, and
// flags, so the kernel never names a command.
internal sealed class ItemTransactionKernel(
    NpgsqlDataSource dataSource,
    ItemTransactionTelemetry telemetry,
    ItemOperationLedger ledger,
    ItemSimulationAuthority simulationAuthority,
    CharacterItemStateLocks characterStateLocks,
    ItemOperationsOptions? operationsOptions = null,
    ItemOperationsMetrics? operationsMetrics = null)
{
    private const string MutationSavepoint = "item_mutation";

    private readonly ItemOperationsOptions operationsOptions =
        operationsOptions ?? ItemOperationsOptions.CreateDefaults();

    public async Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        ItemCommandRegistration<TCommand> registration,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        var started = Stopwatch.GetTimestamp();
        var correlationId = telemetry.ResolveCorrelationId(request.OperationId);
        try
        {
            var result = await ExecuteCoreAsync(
                request,
                registration,
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(started);
            telemetry.RecordCompleted(
                registration,
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
                registration,
                request.OperationId,
                Stopwatch.GetElapsedTime(started),
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested,
                correlationId);
            throw;
        }
    }

    private async Task<ItemTransactionResult> ExecuteCoreAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        ItemCommandRegistration<TCommand> registration,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(request.Actor);
        ArgumentNullException.ThrowIfNull(request.Command);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("An item operation id is required.", nameof(request));
        }

        var operationKind = registration.OperationKind;
        var actorCharacterId = registration.ActorCharacter(request.Command);
        if (ItemOperationAdmission.RequiresRandomClientOperationId(request.Actor, registration)
            && !ItemOperationIds.IsRandomClientOperationId(request.OperationId))
        {
            return ItemTransactionOutcomes.CreateRejected(
                request.OperationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationIdInvalid,
                "The item operation id must be a random version 4 UUID.");
        }

        var canonicalRequest = ItemOperationRequestCanonicalizer.Canonicalize(
            registration,
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
        var lifetime = new TransactionLifetime(transaction);

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
                characterStateLocks,
                operationsMetrics);
            try
            {
                ItemActorAuthority.EnsureValid(session.Actor);
                await simulationAuthority.ValidateLiveSessionAsync(
                    session,
                    actorCharacterId,
                    registration.MayRunAfterAccountSessionEnd,
                    cancellationToken);
            }
            catch (ItemTransactionRejectedException exception)
            {
                await lifetime.RollbackAsync(cancellationToken);
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
                await lifetime.CommitAsync(cancellationToken);
                return replay;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"savepoint {MutationSavepoint};",
                transaction: transaction,
                cancellationToken: cancellationToken));

            try
            {
                await registration.Handler.ExecuteAsync(context, request.Command, cancellationToken);
                var result = await new ItemTransactionFinalizer(session, context.Journal)
                    .FinalizeAsync(cancellationToken);
                await ledger.CompleteAsync(
                    connection,
                    transaction,
                    result,
                    "committed",
                    cancellationToken);
                await lifetime.CommitAsync(cancellationToken);
                return result;
            }
            catch (ItemTransactionRejectedException exception)
            {
                return await RejectMutationAsync(
                    connection,
                    lifetime,
                    request.OperationId,
                    registration,
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

                // A deferred constraint that fails at COMMIT has already ended the
                // transaction, so its rejection cannot be stored.
                if (lifetime.HasEnded)
                {
                    return ItemTransactionOutcomes.CreateRejected(
                        request.OperationId,
                        operationKind,
                        mapped.Code,
                        mapped.Message);
                }

                return await RejectMutationAsync(
                    connection,
                    lifetime,
                    request.OperationId,
                    registration,
                    mapped.Code,
                    mapped.Message,
                    cancellationToken);
            }
        }
        catch (PostgresException exception)
        {
            await lifetime.RollbackIfActiveAsync();
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
            await lifetime.RollbackIfActiveAsync();
            throw;
        }
    }

    private async Task<ItemTransactionResult> RejectMutationAsync(
        NpgsqlConnection connection,
        TransactionLifetime lifetime,
        Guid operationId,
        ItemCommandRegistration registration,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // A transient database rejection is not an outcome of the request, and a retried
        // system operation must stay claimable, so both release the operation claim.
        if (errorCode == ItemTransactionErrorCodes.ItemTransactionTimeout
            || registration.RetriedSystemOperation)
        {
            await lifetime.RollbackAsync(cancellationToken);
            return ItemTransactionOutcomes.CreateRejected(
                operationId,
                registration.OperationKind,
                errorCode,
                errorMessage);
        }

        return await RollBackMutationAndRejectAsync(
            connection,
            lifetime,
            operationId,
            registration.OperationKind,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    private async Task<ItemTransactionResult> RollBackMutationAndRejectAsync(
        NpgsqlConnection connection,
        TransactionLifetime lifetime,
        Guid operationId,
        string operationKind,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"rollback to savepoint {MutationSavepoint};",
            transaction: lifetime.Transaction,
            cancellationToken: cancellationToken));
        var result = ItemTransactionOutcomes.CreateRejected(
            operationId,
            operationKind,
            errorCode,
            errorMessage);
        await ledger.CompleteAsync(
            connection,
            lifetime.Transaction,
            result,
            "rejected",
            cancellationToken);
        await lifetime.CommitAsync(cancellationToken);
        return result;
    }

    // Tracks whether a commit or rollback was attempted. A failed COMMIT ends the
    // transaction on the server, so an ended transaction is never rolled back again and
    // the original error stays visible.
    private sealed class TransactionLifetime(NpgsqlTransaction transaction)
    {
        public NpgsqlTransaction Transaction { get; } = transaction;

        public bool HasEnded { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            HasEnded = true;
            return Transaction.CommitAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            HasEnded = true;
            return Transaction.RollbackAsync(cancellationToken);
        }

        public Task RollbackIfActiveAsync()
        {
            return HasEnded ? Task.CompletedTask : RollbackAsync(CancellationToken.None);
        }
    }
}
