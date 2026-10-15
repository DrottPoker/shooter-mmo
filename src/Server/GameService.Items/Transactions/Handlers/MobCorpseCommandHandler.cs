using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Creates a durable Mob corpse with its loot for a simulation worker that holds a
// fresh lease on the corpse's Shard.
internal sealed class MobCorpseCommandHandler
    : IItemCommandHandler<CreatePersistentMobCorpseCommand>
{
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        CreatePersistentMobCorpseCommand command,
        CancellationToken cancellationToken)
    {
        ValidatePersistentMobCorpseCommand(command);
        context.Authority.EnsureSystemAuthority();
        await context.ValidateSimulationWorkerServiceAuthorityAsync(
            command.WorkerId,
            command.WorkerRuntimeId,
            command.ShardId,
            command.WorkerHeartbeatTimeoutSeconds,
            cancellationToken);
        await DurableCorpseRecords.LockDeathEventKeyAsync(context, command.CorpseId, cancellationToken);

        var corpseExists = await context.Connection.QuerySingleAsync<bool>(
            new CommandDefinition(
                "select exists (select 1 from corpses where id = @CorpseId);",
                new { command.CorpseId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (corpseExists)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobCorpseInvalid,
                "The durable Mob corpse id was already used by another operation.");
        }

        var definitions = new List<ResolvedItemDefinition>(command.Loot.Count);
        foreach (var loot in command.Loot)
        {
            var definition = await context.LoadDefinitionAsync(
                loot.DefinitionId,
                cancellationToken);
            MobLootRules.ValidateMobLootDefinition(definition, loot.Quantity);
            definitions.Add(definition);
        }

        var generalContainerId = Guid.NewGuid();
        var equipmentContainerId = Guid.NewGuid();
        var bagContainerId = Guid.NewGuid();
        var expiryOperationId = MobLootRules.CreateScopedGuid("mob-corpse-expiry", command.CorpseId);
        var corpseTimes = await DurableCorpseRecords.CreateDurableCorpseRecordAsync(
            context,
            command.CorpseId,
            "persistent_npc",
            null,
            command.SourceActorDefinitionId,
            command.SourceDisplayName,
            command.ShardId,
            command.PositionX,
            command.PositionY,
            command.PositionZ,
            command.RotationX,
            command.RotationY,
            command.RotationZ,
            command.RotationW,
            command.PresentationKey,
            1,
            command.LifetimeSeconds,
            expiryOperationId,
            cancellationToken);

        await CreateMobCorpseContainersAsync(
            context,
            command.CorpseId,
            generalContainerId,
            equipmentContainerId,
            bagContainerId,
            command.Loot.Count,
            cancellationToken);

        for (var index = 0; index < command.Loot.Count; index++)
        {
            var loot = command.Loot[index];
            var definition = definitions[index];
            var itemInstanceId = MobLootRules.CreateScopedGuid("mob-loot-item", loot.GrantId);
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
                    loot.Quantity,
                    ContainerId = generalContainerId,
                    SlotIndex = index
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            var afterState = await context.CaptureItemStateJsonAsync(
                itemInstanceId,
                cancellationToken);
            context.AddItemAudit(
                "persistent_mob_loot_materialized",
                itemInstanceId,
                null,
                afterState);
            context.IncludeResultItem(itemInstanceId);
        }

        context.TouchContainer(generalContainerId);
        context.AddMetadataAudit(
            "persistent_mob_corpse_created",
            null,
            new
            {
                command.CorpseId,
                command.SourceActorDefinitionId,
                command.ShardId,
                command.LifetimeSeconds,
                corpseTimes.CreatedAt,
                corpseTimes.ExpiresAt,
                LootCount = command.Loot.Count
            });
    }

    private static async Task CreateMobCorpseContainersAsync(
        ItemTransactionContext context,
        Guid corpseId,
        Guid generalContainerId,
        Guid equipmentContainerId,
        Guid bagContainerId,
        int lootCount,
        CancellationToken cancellationToken)
    {
        var generalCapacity = Math.Max(1, lootCount);
        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_containers (id, container_type, slot_capacity, lifecycle)
            values
                (@GeneralContainerId, 'corpse_inventory', @GeneralCapacity, 'active'),
                (@EquipmentContainerId, 'corpse_equipment', 1, 'active'),
                (@BagContainerId, 'corpse_bag_contents', 1, 'active');

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @GeneralContainerId, slot_index, 'general'
            from generate_series(0, @GeneralCapacity - 1) slot_index;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            values
                (@EquipmentContainerId, 0, 'general'),
                (@BagContainerId, 0, 'general');

            insert into corpse_sections (corpse_id, section_kind, container_id)
            values
                (@CorpseId, 'general_inventory', @GeneralContainerId),
                (@CorpseId, 'equipment', @EquipmentContainerId),
                (@CorpseId, 'bag', @BagContainerId);
            """,
            new
            {
                CorpseId = corpseId,
                GeneralContainerId = generalContainerId,
                GeneralCapacity = generalCapacity,
                EquipmentContainerId = equipmentContainerId,
                BagContainerId = bagContainerId
            },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    internal static void ValidatePersistentMobCorpseCommand(
        CreatePersistentMobCorpseCommand command)
    {
        if (command.CorpseId == Guid.Empty
            || !MobLootRules.IsMobIdentifier(command.WorkerId)
            || !MobLootRules.IsMobIdentifier(command.WorkerRuntimeId)
            || !MobLootRules.IsMobIdentifier(command.ShardId)
            || !MobLootRules.IsMobIdentifier(command.SourceActorDefinitionId)
            || string.IsNullOrWhiteSpace(command.SourceDisplayName)
            || command.SourceDisplayName.Length > 128
            || !string.Equals(
                command.PresentationKey,
                CorpseTransformRules.PlayerCorpsePresentationKey,
                StringComparison.Ordinal)
            || !double.IsFinite(command.LifetimeSeconds)
            || command.LifetimeSeconds
                is < WorldActorCorpseRules.MinimumLifetimeSeconds
                or > WorldActorCorpseRules.MaximumLifetimeSeconds
            || command.WorkerHeartbeatTimeoutSeconds <= 0
            || !CorpseTransformRules.IsFinitePosition(command.PositionX)
            || !CorpseTransformRules.IsFinitePosition(command.PositionY)
            || !CorpseTransformRules.IsFinitePosition(command.PositionZ)
            || !CorpseTransformRules.IsNormalizedRotation(
                command.RotationX,
                command.RotationY,
                command.RotationZ,
                command.RotationW)
            || command.Loot is null
            || command.Loot.Count is < 1 or > WorldActorCorpseRules.MaximumLootEntries
            || command.Loot.Any(loot =>
                loot.GrantId == Guid.Empty
                || !MobLootRules.IsMobIdentifier(loot.DefinitionId)
                || loot.Quantity <= 0)
            || command.Loot.Select(loot => loot.GrantId).Distinct().Count()
                != command.Loot.Count)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobCorpseInvalid,
                "The persistent Mob corpse command is incomplete or invalid.");
        }
    }
}
