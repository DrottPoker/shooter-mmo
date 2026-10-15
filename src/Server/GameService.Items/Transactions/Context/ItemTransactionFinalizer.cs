using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// Kernel-owned finalization of a successful handler: container revisions, carry
// weight and the hard cap, character revisions, result item revisions, and audit rows,
// in this order. It fails a transaction whose handler recorded no audit change.
internal sealed class ItemTransactionFinalizer(
    ItemTransactionSession session,
    ItemChangeJournal journal)
{
    private readonly CarryStateCalculator carryStates = new(session);

    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction;

    private Guid OperationId => session.OperationId;

    private string OperationKind => session.OperationKind;

    public async Task<ItemTransactionResult> FinalizeAsync(
        CancellationToken cancellationToken)
    {
        if (journal.AuditChanges.Count == 0)
        {
            throw new InvalidOperationException(
                $"Item operation '{OperationKind}' produced no audit changes.");
        }

        var containerIds = journal.TouchedContainerIds.Order().ToArray();
        if (containerIds.Length > 0)
        {
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                update item_containers
                set revision = revision + 1,
                    updated_at = now()
                where id = any(@ContainerIds);
                """,
                new { ContainerIds = containerIds },
                Transaction,
                cancellationToken: cancellationToken));
        }

        var containerResults = containerIds.Length == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionContainerRevision>(
                new CommandDefinition(
                    """
                    select
                        id as "ContainerId",
                        revision as "Revision"
                    from item_containers
                    where id = any(@ContainerIds)
                    order by id;
                    """,
                    new { ContainerIds = containerIds },
                    Transaction,
                    cancellationToken: cancellationToken))).ToArray();

        var characterResults = new List<ItemTransactionCharacterRevision>();
        foreach (var characterId in journal.TouchedCharacterIds.Order())
        {
            var carry = await carryStates.CalculateCarryStateAsync(characterId, cancellationToken);
            var previousCarry = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    """
                    select
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity"
                    from character_item_states
                    where character_id = @CharacterId;
                    """,
                    new { CharacterId = characterId },
                    Transaction,
                    cancellationToken: cancellationToken));
            if (!journal.AllowsInvoluntaryHardCapOverflow
                && !EncumbranceRules.IsAllowedVoluntaryTransition(
                    previousCarry.CarriedWeight,
                    previousCarry.CarryCapacity,
                    carry.CarriedWeight,
                    carry.CarryCapacity))
            {
                Reject(
                    ItemTransactionErrorCodes.CarryWeightLimitExceeded,
                    "The operation would exceed the character carry-weight hard cap.");
            }

            var state = await Connection.QuerySingleAsync<CharacterCarryResult>(
                new CommandDefinition(
                    """
                    update character_item_states
                    set revision = revision + 1,
                        carried_weight = @CarriedWeight,
                        carry_capacity = @CarryCapacity,
                        updated_at = now()
                    where character_id = @CharacterId
                    returning
                        revision as "Revision",
                        carried_weight as "CarriedWeight",
                        carry_capacity as "CarryCapacity";
                    """,
                    new
                    {
                        CharacterId = characterId,
                        carry.CarriedWeight,
                        carry.CarryCapacity
                    },
                    Transaction,
                    cancellationToken: cancellationToken));
            characterResults.Add(new ItemTransactionCharacterRevision(
                characterId,
                state.Revision,
                state.CarriedWeight,
                state.CarryCapacity));
        }

        var resultItems = journal.ResultItemIds.Count == 0
            ? []
            : (await Connection.QueryAsync<ItemTransactionItemRevision>(new CommandDefinition(
                """
                select
                    id as "ItemInstanceId",
                    revision as "Revision"
                from item_instances
                where id = any(@ItemIds)
                order by id;
                """,
                new { ItemIds = journal.ResultItemIds.Order().ToArray() },
                Transaction,
                cancellationToken: cancellationToken))).ToArray();

        for (var index = 0; index < journal.AuditChanges.Count; index++)
        {
            var change = journal.AuditChanges[index];
            await Connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_operation_changes (
                    operation_id,
                    change_index,
                    item_instance_id,
                    change_kind,
                    before_state,
                    after_state)
                values (
                    @OperationId,
                    @ChangeIndex,
                    @ItemInstanceId,
                    @ChangeKind,
                    cast(@BeforeState as jsonb),
                    cast(@AfterState as jsonb));
                """,
                new
                {
                    OperationId,
                    ChangeIndex = index,
                    change.ItemInstanceId,
                    change.ChangeKind,
                    change.BeforeState,
                    change.AfterState
                },
                Transaction,
                cancellationToken: cancellationToken));
        }

        return new ItemTransactionResult(
            OperationId,
            OperationKind,
            true,
            null,
            characterResults,
            containerResults,
            resultItems,
            journal.RecoveryDeliveryIds.Order().ToArray(),
            journal.SecureContainerEntitlementRevision);
    }

    private sealed class CharacterCarryResult
    {
        public long Revision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }
    }
}
