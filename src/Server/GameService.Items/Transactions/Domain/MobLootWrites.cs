using Dapper;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The item writes of a live Mob loot grant: a new item under the id derived from its grant id,
// in a container slot or an equipment slot, or a top-up of an existing stack, each with its
// audit rows.
internal static class MobLootWrites
{
    public static Guid ItemInstanceId(Guid grantId)
    {
        return ScopedGuid.Create("mob-loot-item", grantId);
    }

    public static async Task CreateInContainerAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        ResolvedItemDefinition definition,
        int quantity,
        Guid containerId,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        var itemInstanceId = ItemInstanceId(grant.GrantId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                container_id,
                container_slot_index)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @ContainerId,
                @SlotIndex);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                Quantity = quantity,
                ContainerId = containerId,
                SlotIndex = slotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalAsync(context, grant, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
        context.Journal.TouchContainer(containerId);
    }

    public static async Task CreateEquippedAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        ResolvedItemDefinition definition,
        int quantity,
        string equipmentSlotId,
        CancellationToken cancellationToken)
    {
        var itemInstanceId = ItemInstanceId(grant.GrantId);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_instances (
                id,
                definition_id,
                quantity,
                equipped_character_id,
                equipment_slot_id)
            values (
                @ItemInstanceId,
                @DefinitionId,
                @Quantity,
                @CharacterId,
                @EquipmentSlotId);
            """,
            new
            {
                ItemInstanceId = itemInstanceId,
                DefinitionId = definition.RuntimeDefinition.Id,
                Quantity = quantity,
                grant.CharacterId,
                EquipmentSlotId = equipmentSlotId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalAsync(context, grant, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
    }

    public static async Task TopUpAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        Guid targetItemInstanceId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var beforeState = await context.Journal.CaptureItemStateJsonAsync(
            targetItemInstanceId,
            cancellationToken);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update item_instances
            set quantity = quantity + @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new { Quantity = quantity, ItemInstanceId = targetItemInstanceId },
            context.Transaction,
            cancellationToken: cancellationToken));
        await JournalAsync(
            context,
            grant,
            targetItemInstanceId,
            "live_mob_loot_merged",
            beforeState,
            cancellationToken);
    }

    private static async Task JournalAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        Guid itemInstanceId,
        string auditKind,
        string? beforeState,
        CancellationToken cancellationToken)
    {
        var afterState = await context.Journal.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            itemInstanceId,
            beforeState,
            afterState);
        context.Journal.AddMetadataAudit(
            "live_mob_loot_grant_committed",
            null,
            new
            {
                grant.GrantId,
                grant.SourceCorpseId,
                grant.SourceActorDefinitionId,
                grant.CharacterId,
                ItemInstanceId = itemInstanceId
            });
        context.Journal.TouchCharacter(grant.CharacterId);
        context.Journal.IncludeResultItem(itemInstanceId);
    }
}

// Where one live Mob loot grant comes from and who receives it.
internal sealed record MobLootGrantSource(
    Guid GrantId,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    Guid CharacterId);
