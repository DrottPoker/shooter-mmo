using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Swaps a corpse's equipped Bag aggregate with the looter's equipped Bag aggregate.
internal sealed class CorpseBagSwapCommandHandler
    : IItemCommandHandler<SwapCorpseBagCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        SwapCorpseBagCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCorpseBagCommand(command);
        await context.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var corpse = await CorpseCustody.LockOpenCorpseAsync(
            context,
            command.CorpseId,
            cancellationToken);
        await context.LockMutationScopeAsync(
            [command.CorpseBagItemInstanceId, command.PlayerBagItemInstanceId],
            [command.CorpseBagContentsContainerId, command.PlayerBagContentsContainerId],
            cancellationToken);

        var corpseBag = await context.LoadItemAsync(
            command.CorpseBagItemInstanceId,
            cancellationToken);
        var playerBag = await context.LoadItemAsync(
            command.PlayerBagItemInstanceId,
            cancellationToken);
        var sections = await LoadCorpseBagSectionAsync(
            context,
            command.CorpseId,
            cancellationToken);
        if (corpseBag is null
            || !corpseBag.IsBag
            || corpseBag.Revision != command.ExpectedCorpseBagRevision
            || corpseBag.ContainerId != sections.EquipmentContainerId
            || corpseBag.ContainerSlotIndex is null
            || corpseBag.BagContentsContainerId != command.CorpseBagContentsContainerId
            || corpseBag.BagContentsContainerId != sections.BagContainerId
            || corpseBag.BagContentsRevision != command.ExpectedCorpseBagContentsRevision
            || !string.Equals(corpseBag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag aggregate changed before the swap committed.");
        }

        if (playerBag is null
            || !playerBag.IsBag
            || playerBag.OwningCharacterId != command.CharacterId
            || playerBag.Revision != command.ExpectedPlayerBagRevision
            || playerBag.EquippedCharacterId != command.CharacterId
            || !string.Equals(
                playerBag.EquipmentSlotId,
                ItemEquipmentSlotIds.Bag,
                StringComparison.Ordinal)
            || playerBag.BagContentsContainerId != command.PlayerBagContentsContainerId
            || playerBag.BagContentsRevision != command.ExpectedPlayerBagContentsRevision
            || !string.Equals(playerBag.BagContentsLifecycle, "active", StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The equipped player Bag aggregate changed before the swap committed.");
        }

        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, corpseBag, cancellationToken);
        await BagAggregateGuards.EnsureBagAggregateTransferAllowedAsync(context, playerBag, cancellationToken);

        var corpseBagBefore = await context.CaptureItemStateJsonAsync(
            corpseBag.ItemInstanceId,
            cancellationToken);
        var playerBagBefore = await context.CaptureItemStateJsonAsync(
            playerBag.ItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            set constraints ux_item_instances_equipment_slot deferred;
            set constraints ux_item_instances_container_slot deferred;

            update item_instances
            set container_id = case id
                    when @CorpseBagItemInstanceId then null
                    when @PlayerBagItemInstanceId then @CorpseEquipmentContainerId
                end,
                container_slot_index = case id
                    when @CorpseBagItemInstanceId then null
                    when @PlayerBagItemInstanceId then @CorpseEquipmentSlotIndex
                end,
                equipped_character_id = case id
                    when @CorpseBagItemInstanceId then @CharacterId
                    when @PlayerBagItemInstanceId then null
                end,
                equipment_slot_id = case id
                    when @CorpseBagItemInstanceId then 'bag'
                    when @PlayerBagItemInstanceId then null
                end,
                revision = revision + 1,
                updated_at = now()
            where id in (@CorpseBagItemInstanceId, @PlayerBagItemInstanceId);

            update corpse_sections
            set container_id = @PlayerBagContentsContainerId
            where corpse_id = @CorpseId
              and section_kind = 'bag';
            """,
            new
            {
                command.CorpseBagItemInstanceId,
                command.PlayerBagItemInstanceId,
                CorpseEquipmentContainerId = sections.EquipmentContainerId,
                CorpseEquipmentSlotIndex = corpseBag.ContainerSlotIndex.Value,
                command.CharacterId,
                command.PlayerBagContentsContainerId,
                command.CorpseId
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        var corpseBagAfter = await context.CaptureItemStateJsonAsync(
            corpseBag.ItemInstanceId,
            cancellationToken);
        var playerBagAfter = await context.CaptureItemStateJsonAsync(
            playerBag.ItemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "corpse_bag_swapped_to_character",
            corpseBag.ItemInstanceId,
            corpseBagBefore,
            corpseBagAfter);
        context.AddItemAudit(
            "character_bag_swapped_to_corpse",
            playerBag.ItemInstanceId,
            playerBagBefore,
            playerBagAfter);
        await CorpseCustody.AdvanceCorpseRevisionAsync(context, corpse, cancellationToken);
        context.TouchContainer(sections.EquipmentContainerId);
        context.TouchContainer(corpseBag.BagContentsContainerId);
        context.TouchContainer(playerBag.BagContentsContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(corpseBag.ItemInstanceId);
        context.IncludeResultItem(playerBag.ItemInstanceId);
    }

    private static async Task<CorpseBagSectionRow> LoadCorpseBagSectionAsync(
        ItemTransactionContext context,
        Guid corpseId,
        CancellationToken cancellationToken)
    {
        var sections = await context.Connection.QuerySingleAsync<CorpseBagSectionRow>(
            new CommandDefinition(
                """
                select
                    (
                        select container_id
                        from corpse_sections
                        where corpse_id = @CorpseId
                          and section_kind = 'equipment') as "EquipmentContainerId",
                    (
                        select container_id
                        from corpse_sections
                        where corpse_id = @CorpseId
                          and section_kind = 'bag') as "BagContainerId";
                """,
                new { CorpseId = corpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (sections.EquipmentContainerId == Guid.Empty
            || sections.BagContainerId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag sections are unavailable.");
        }

        return sections;
    }

    private static void ValidateCorpseBagCommand(SwapCorpseBagCommand command)
    {
        if (command.CharacterId == Guid.Empty
            || command.CorpseId == Guid.Empty
            || command.CorpseBagItemInstanceId == Guid.Empty
            || command.ExpectedCorpseBagRevision < 0
            || command.CorpseBagContentsContainerId == Guid.Empty
            || command.ExpectedCorpseBagContentsRevision < 0
            || command.PlayerBagItemInstanceId == Guid.Empty
            || command.ExpectedPlayerBagRevision < 0
            || command.PlayerBagContentsContainerId == Guid.Empty
            || command.ExpectedPlayerBagContentsRevision < 0
            || command.CorpseBagItemInstanceId == command.PlayerBagItemInstanceId
            || command.CorpseBagContentsContainerId == command.PlayerBagContentsContainerId)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.BagStateChanged,
                "The corpse Bag swap command is incomplete or invalid.");
        }
    }

    private sealed class CorpseBagSectionRow
    {
        public Guid EquipmentContainerId { get; set; }

        public Guid BagContainerId { get; set; }
    }
}
