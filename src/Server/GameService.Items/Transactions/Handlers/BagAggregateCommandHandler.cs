using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Swaps two equipped Bags together with their contents between two characters.
internal sealed class BagAggregateCommandHandler
    : IItemCommandHandler<SwapBagAggregatesCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        SwapBagAggregatesCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FirstCharacterId == command.SecondCharacterId
            || command.FirstBagItemInstanceId == command.SecondBagItemInstanceId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "A Bag aggregate swap requires two distinct characters and Bag items.");
        }

        await context.Locks.LockCharacterStatesAsync(
            [
                new CharacterLockRequest(
                    command.FirstCharacterId,
                    command.ExpectedFirstCharacterRevision),
                new CharacterLockRequest(
                    command.SecondCharacterId,
                    command.ExpectedSecondCharacterRevision)
            ],
            cancellationToken);
        await context.Locks.LockMutationScopeAsync(
            [command.FirstBagItemInstanceId, command.SecondBagItemInstanceId],
            [],
            cancellationToken);
        var firstBag = await context.State.LoadOwnedItemAsync(
            command.FirstBagItemInstanceId,
            command.FirstCharacterId,
            command.ExpectedFirstBagRevision,
            cancellationToken);
        var secondBag = await context.State.LoadOwnedItemAsync(
            command.SecondBagItemInstanceId,
            command.SecondCharacterId,
            command.ExpectedSecondBagRevision,
            cancellationToken);
        BagAggregateGuards.EnsureEquippedBagAggregate(
            firstBag,
            command.FirstCharacterId,
            command.ExpectedFirstBagContentsRevision);
        BagAggregateGuards.EnsureEquippedBagAggregate(
            secondBag,
            command.SecondCharacterId,
            command.ExpectedSecondBagContentsRevision);
        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, firstBag, cancellationToken);
        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, secondBag, cancellationToken);

        var beforeFirst = await context.Journal.CaptureItemStateJsonAsync(
            firstBag.ItemInstanceId,
            cancellationToken);
        var beforeSecond = await context.Journal.CaptureItemStateJsonAsync(
            secondBag.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_equipment_slot deferred;

            update item_instances
            set equipped_character_id = case id
                    when @FirstBagItemInstanceId then @SecondCharacterId
                    when @SecondBagItemInstanceId then @FirstCharacterId
                end,
                revision = revision + 1,
                updated_at = now()
            where id in (@FirstBagItemInstanceId, @SecondBagItemInstanceId);
            """,
            new
            {
                FirstBagItemInstanceId = firstBag.ItemInstanceId,
                SecondBagItemInstanceId = secondBag.ItemInstanceId,
                command.FirstCharacterId,
                command.SecondCharacterId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterFirst = await context.Journal.CaptureItemStateJsonAsync(
            firstBag.ItemInstanceId,
            cancellationToken);
        var afterSecond = await context.Journal.CaptureItemStateJsonAsync(
            secondBag.ItemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            "bag_aggregate_swapped",
            firstBag.ItemInstanceId,
            beforeFirst,
            afterFirst);
        context.Journal.AddItemAudit(
            "bag_aggregate_swapped",
            secondBag.ItemInstanceId,
            beforeSecond,
            afterSecond);
        context.Journal.TouchContainer(firstBag.BagContentsContainerId);
        context.Journal.TouchContainer(secondBag.BagContentsContainerId);
        context.Journal.TouchCharacter(command.FirstCharacterId);
        context.Journal.TouchCharacter(command.SecondCharacterId);
        context.Journal.IncludeResultItem(firstBag.ItemInstanceId);
        context.Journal.IncludeResultItem(secondBag.ItemInstanceId);
    }
}
