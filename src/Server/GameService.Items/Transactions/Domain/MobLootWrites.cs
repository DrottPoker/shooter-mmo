using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// The item writes of a live Mob loot grant: a new item under the id derived from its grant id,
// in a container slot or an equipment slot, or a top-up of an existing stack, each with its
// audit rows. A quest drop is created with its quest's lineage.
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
        var create = new PostgresBatch(context.Scope);
        create.Execute(
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
            });
        await JournalAsync(context, create, grant, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
        context.Journal.TouchContainer(containerId);
    }

    public static async Task CreateQuestDropInContainerAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        ResolvedItemDefinition definition,
        int quantity,
        Guid containerId,
        int slotIndex,
        string questId,
        CancellationToken cancellationToken)
    {
        var itemInstanceId = ItemInstanceId(grant.GrantId);
        var create = new PostgresBatch(context.Scope);
        await NewItemInstances.CreateAsync(
            context,
            create,
            itemInstanceId,
            definition,
            quantity,
            containerId,
            slotIndex,
            ItemPolicySourceKinds.QuestGrant,
            questId,
            cancellationToken);
        await JournalAsync(context, create, grant, itemInstanceId, "live_mob_quest_drop_granted", null, cancellationToken);
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
        var create = new PostgresBatch(context.Scope);
        create.Execute(
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
            });
        await JournalAsync(context, create, grant, itemInstanceId, "live_mob_loot_granted", null, cancellationToken);
    }

    public static async Task TopUpAsync(
        ItemTransactionContext context,
        MobLootGrantSource grant,
        Guid targetItemInstanceId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var topUp = new PostgresBatch(context.Scope);
        var beforeState = context.Journal.CaptureItemStateJson(topUp, targetItemInstanceId);
        topUp.Execute(
            """
            update item_instances
            set quantity = quantity + @Quantity,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemInstanceId;
            """,
            new { Quantity = quantity, ItemInstanceId = targetItemInstanceId });
        await JournalAsync(
            context,
            topUp,
            grant,
            targetItemInstanceId,
            "live_mob_loot_merged",
            beforeState,
            cancellationToken);
    }

    // The write in the batch, the item's state after it, and the audit rows, in one round trip.
    private static async Task JournalAsync(
        ItemTransactionContext context,
        PostgresBatch write,
        MobLootGrantSource grant,
        Guid itemInstanceId,
        string auditKind,
        PostgresBatchResult<string?>? beforeState,
        CancellationToken cancellationToken)
    {
        var afterState = context.Journal.CaptureItemStateJson(write, itemInstanceId);
        await write.ExecuteAsync(cancellationToken);
        context.Journal.AddItemAudit(
            auditKind,
            itemInstanceId,
            beforeState?.Value,
            afterState.Value);
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
