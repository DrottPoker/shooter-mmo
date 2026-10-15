using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
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

    public async Task<ItemTransactionResult?> ClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        ItemTransactionActor actor,
        Guid? actorCharacterId,
        string operationKind,
        string requestHash,
        string requestPayload,
        CancellationToken cancellationToken)
    {
        var claimed = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
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
            },
            transaction,
            cancellationToken: cancellationToken));
        if (claimed is not null)
        {
            return null;
        }

        var existing = await connection.QuerySingleAsync<OperationRow>(new CommandDefinition(
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

    public Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemTransactionResult result,
        string status,
        CancellationToken cancellationToken)
    {
        var resultPayload = JsonSerializer.Serialize(result, ItemOperationJson.Options);
        return connection.ExecuteAsync(new CommandDefinition(
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
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class OperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ResultPayload { get; set; }
    }
}
