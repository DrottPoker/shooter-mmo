using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Transactions;

// The only owner of item_operations SQL in the transaction kernel: the idempotent claim,
// its completion, and the committed-result replay after a live authority rejection.
internal sealed class ItemOperationLedger(NpgsqlDataSource dataSource)
{
    // Opens its own connection because the caller's transaction was already rolled back.
    public async Task<ItemTransactionResult?> LoadCommittedReplayAsync(
        Guid operationId,
        string operationKind,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            select result_payload::text
            from item_operations
            where operation_id = @OperationId
              and operation_kind = @OperationKind
              and request_hash = @RequestHash
              and status = 'committed';
            """,
            new
            {
                OperationId = operationId,
                OperationKind = operationKind,
                RequestHash = requestHash
            },
            cancellationToken: cancellationToken));
        return payload is null
            ? null
            : JsonSerializer.Deserialize<ItemTransactionResult>(payload, ItemOperationJson.Options);
    }

    // Claims the operation in the round trip of the statements that the batch still holds, which
    // never decide whether the claim may run.
    public async Task<ItemTransactionResult?> ClaimAsync(
        PostgresBatch batch,
        Guid operationId,
        ItemTransactionActor actor,
        Guid? actorCharacterId,
        string operationKind,
        string requestHash,
        string requestPayload,
        CancellationToken cancellationToken)
    {
        var claim = batch.QuerySingleOrDefault<int?>(
            $"""
            insert into item_operations (
                operation_id,
                actor_account_id,
                actor_character_id,
                operation_kind,
                request_hash,
                request_payload)
            values (
                @OperationId,
                {AccountsSql.ExistingIdOrNull("cast(@ActorAccountId as uuid)")},
                {CharactersSql.ExistingIdOrNull("cast(@ActorCharacterId as uuid)")},
                @OperationKind,
                @RequestHash,
                cast(@RequestPayload as jsonb))
            on conflict (operation_id) do nothing
            returning 1;
            """,
            new
            {
                OperationId = operationId,
                ActorAccountId = actor.AccountId,
                ActorCharacterId = actorCharacterId,
                OperationKind = operationKind,
                RequestHash = requestHash,
                RequestPayload = requestPayload
            });
        await batch.ExecuteAsync(cancellationToken);
        if (claim.Value is not null)
        {
            return null;
        }

        var connection = batch.Scope.Connection;
        var transaction = batch.Scope.Transaction;
        var existing = await connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
            """
            select
                operation_kind as "OperationKind",
                request_hash as "RequestHash",
                status as "Status",
                result_payload::text as "ResultPayload"
            from item_operations
            where operation_id = @OperationId
            for update;
            """,
            new { OperationId = operationId },
            transaction,
            cancellationToken: cancellationToken));
        if (existing is null)
        {
            // Retention removed the conflicting row after the insert saw it. Nothing is
            // claimed, so the caller retries the same operation id.
            return ItemTransactionOutcomes.CreateRejected(
                operationId,
                operationKind,
                ItemTransactionErrorCodes.ItemTransactionTimeout,
                "The item transaction was aborted by concurrent database work. Retry it.");
        }

        if (!string.Equals(existing.OperationKind, operationKind, StringComparison.Ordinal)
            || !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return ItemTransactionOutcomes.CreateRejected(
                operationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The operation id was already used with a different request.");
        }

        if (string.Equals(existing.Status, "pending", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(existing.ResultPayload))
        {
            return ItemTransactionOutcomes.CreateRejected(
                operationId,
                operationKind,
                ItemTransactionErrorCodes.ItemOperationConflict,
                "The operation id is already pending.");
        }

        return JsonSerializer.Deserialize<ItemTransactionResult>(
                existing.ResultPayload,
                ItemOperationJson.Options)
            ?? throw new InvalidOperationException(
                $"Stored result for item operation '{operationId}' is invalid.");
    }

    // Completes the operation in the round trip of the statements that the batch still holds,
    // such as the finalizer's audit rows or the rollback to the mutation savepoint.
    public Task CompleteAsync(
        PostgresBatch batch,
        ItemTransactionResult result,
        string status,
        CancellationToken cancellationToken)
    {
        var resultPayload = JsonSerializer.Serialize(result, ItemOperationJson.Options);
        batch.Execute(
            """
            update item_operations
            set status = @Status,
                result_payload = cast(@ResultPayload as jsonb),
                revision = revision + 1,
                completed_at = now()
            where operation_id = @OperationId;
            """,
            new
            {
                result.OperationId,
                Status = status,
                ResultPayload = resultPayload
            });
        return batch.ExecuteAsync(cancellationToken);
    }

    private sealed class OperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ResultPayload { get; set; }
    }
}
