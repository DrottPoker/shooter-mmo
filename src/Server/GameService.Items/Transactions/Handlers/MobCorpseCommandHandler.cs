using Dapper;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Creates a durable Mob corpse with its loot for a simulation worker that holds a
// fresh lease on the corpse's Shard. A quest drop for the corpse's owner takes its quest's
// lineage and only the quantity that the quest still needs; one the owner does not need is left
// out, and a corpse that keeps no loot is not created.
internal sealed class MobCorpseCommandHandler(ItemSimulationAuthority simulationAuthority, QuestDrops questDrops)
    : IItemCommandHandler<CreatePersistentMobCorpseCommand>
{
    private readonly ItemSimulationAuthority simulationAuthority = simulationAuthority;
    private readonly QuestDrops questDrops = questDrops;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        CreatePersistentMobCorpseCommand command,
        CancellationToken cancellationToken)
    {
        ValidatePersistentMobCorpseCommand(command);
        context.Authority.EnsureSystemAuthority();
        await simulationAuthority.ValidateWorkerServiceLeaseAsync(
            context.Session,
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

        var kept = new List<KeptLoot>(command.Loot.Count);
        var questDropPlan = questDrops.Plan();
        // The quest drops that the owner needs fill the entries that the Mob's own loot leaves,
        // in request order, so a drop that is not needed never takes the place of one that is.
        var questDropRoom = WorldActorCorpseRules.MaximumLootEntries
            - command.Loot.Count(loot => loot.QuestId is null);
        foreach (var loot in command.Loot)
        {
            var definition = await context.State.LoadDefinitionAsync(
                loot.DefinitionId,
                cancellationToken);
            if (loot.QuestId is null)
            {
                MobLootRules.ValidateMobLootDefinition(definition, loot.Quantity);
                kept.Add(new KeptLoot(loot, definition, loot.Quantity));
                continue;
            }

            var ownerId = command.OwnerCharacterId;
            if (ownerId is null
                || !QuestDrops.IsQuestDropItem(definition)
                || loot.Quantity > definition.RuntimeDefinition.MaximumStackSize)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.MobCorpseInvalid,
                    "A quest drop must be a protected quest item within its stack limit for the corpse's owner.");
            }

            if (questDropRoom == 0)
            {
                continue;
            }

            var needed = await questDropPlan.TakeAsync(
                context,
                ownerId!.Value,
                loot.QuestId,
                command.SourceActorDefinitionId,
                definition,
                loot.Quantity,
                cancellationToken);
            if (needed > 0)
            {
                kept.Add(new KeptLoot(loot, definition, needed));
                questDropRoom--;
            }
        }

        if (kept.Count == 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobCorpseEmpty,
                "No loot is left for the durable Mob corpse.");
        }

        var generalContainerId = Guid.NewGuid();
        var equipmentContainerId = Guid.NewGuid();
        var bagContainerId = Guid.NewGuid();
        var expiryOperationId = ScopedGuid.Create("mob-corpse-expiry", command.CorpseId);
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
            command.OwnerCharacterId,
            command.LootGroupId,
            cancellationToken);

        await CreateMobCorpseContainersAsync(
            context,
            command.CorpseId,
            generalContainerId,
            equipmentContainerId,
            bagContainerId,
            kept.Count,
            cancellationToken);

        for (var index = 0; index < kept.Count; index++)
        {
            var (loot, definition, quantity) = kept[index];
            var itemInstanceId = ScopedGuid.Create("mob-loot-item", loot.GrantId);
            if (loot.QuestId is not null)
            {
                await NewItemInstances.CreateAsync(
                    context,
                    itemInstanceId,
                    definition,
                    quantity,
                    generalContainerId,
                    index,
                    ItemPolicySourceKinds.QuestGrant,
                    loot.QuestId,
                    cancellationToken);
            }
            else
            {
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
                    ContainerId = generalContainerId,
                    SlotIndex = index
                },
                context.Transaction,
                cancellationToken: cancellationToken));
            }

            var afterState = await context.Journal.CaptureItemStateJsonAsync(
                itemInstanceId,
                cancellationToken);
            context.Journal.AddItemAudit(
                "persistent_mob_loot_materialized",
                itemInstanceId,
                null,
                afterState);
            context.Journal.IncludeResultItem(itemInstanceId);
        }

        context.Journal.TouchContainer(generalContainerId);
        context.Journal.AddMetadataAudit(
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
                LootCount = kept.Count
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
                (@EquipmentContainerId, 'corpse_equipment', 0, 'active'),
                (@BagContainerId, 'corpse_bag_contents', 0, 'active');

            -- A Mob corpse carries only general loot.
            insert into item_container_slots (container_id, slot_index, slot_kind)
            select @GeneralContainerId, slot_index, 'general'
            from generate_series(0, @GeneralCapacity - 1) slot_index;

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

    private sealed record KeptLoot(MobCorpseLootEntryRequest Loot, ResolvedItemDefinition Definition, int Quantity);

    internal static void ValidatePersistentMobCorpseCommand(
        CreatePersistentMobCorpseCommand command)
    {
        if (command.CorpseId == Guid.Empty
            || !Identifiers.IsServiceIdentifier(command.WorkerId)
            || !Identifiers.IsServiceIdentifier(command.WorkerRuntimeId)
            || !Identifiers.IsServiceIdentifier(command.ShardId)
            || !Identifiers.IsContentIdentifier(command.SourceActorDefinitionId)
            || string.IsNullOrWhiteSpace(command.SourceDisplayName)
            || command.SourceDisplayName.Length > 128
            || !CorpseTransformRules.IsMobCorpsePresentationKey(command.PresentationKey)
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
            || command.Loot.Count < 1
            || command.Loot.Count(loot => loot.QuestId is null) > WorldActorCorpseRules.MaximumLootEntries
            || command.Loot.Count(loot => loot.QuestId is not null) > WorldActorCorpseRules.MaximumQuestDropCandidates
            || command.Loot.Any(loot =>
                loot.GrantId == Guid.Empty
                || !Identifiers.IsContentIdentifier(loot.DefinitionId)
                || loot.Quantity <= 0
                || (loot.QuestId is not null && !Identifiers.IsContentIdentifier(loot.QuestId)))
            || command.Loot.Select(loot => loot.GrantId).Distinct().Count()
                != command.Loot.Count)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobCorpseInvalid,
                "The persistent Mob corpse command is incomplete or invalid.");
        }
    }
}
