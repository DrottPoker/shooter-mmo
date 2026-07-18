using System.Security.Cryptography;
using System.Text;
using Dapper;
using ShooterMmo.WorldData.Actors;

namespace AuthService.Items;

public sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<CreatePersistentMobCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.CreatePersistentMobCorpse,
            null,
            ExecuteCreatePersistentMobCorpseAsync,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantMobLootCommand> request,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(
            request,
            ItemOperationKinds.GrantMobLoot,
            request.Command.CharacterId,
            ExecuteGrantMobLootAsync,
            cancellationToken);
    }

    private static async Task ExecuteCreatePersistentMobCorpseAsync(
        ItemTransactionContext context,
        CreatePersistentMobCorpseCommand command,
        CancellationToken cancellationToken)
    {
        ValidatePersistentMobCorpseCommand(command);
        context.EnsureSystemAuthority();
        await context.ValidateSimulationWorkerServiceAuthorityAsync(
            command.WorkerId,
            command.WorkerRuntimeId,
            command.ShardId,
            command.WorkerHeartbeatTimeoutSeconds,
            cancellationToken);
        await LockDeathEventKeyAsync(context, command.CorpseId, cancellationToken);

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
            ValidateMobLootDefinition(definition, loot.Quantity);
            definitions.Add(definition);
        }

        var generalContainerId = Guid.NewGuid();
        var equipmentContainerId = Guid.NewGuid();
        var bagContainerId = Guid.NewGuid();
        var expiryOperationId = CreateScopedGuid("mob-corpse-expiry", command.CorpseId);
        var corpseTimes = await CreateDurableCorpseRecordAsync(
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
            var itemInstanceId = CreateScopedGuid("mob-loot-item", loot.GrantId);
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

    private static async Task ExecuteGrantMobLootAsync(
        ItemTransactionContext context,
        GrantMobLootCommand command,
        CancellationToken cancellationToken)
    {
        if (context.Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || command.GrantId == Guid.Empty
            || command.GrantId != context.OperationId
            || command.SourceCorpseId == Guid.Empty
            || !IsMobIdentifier(command.SourceActorDefinitionId)
            || command.CharacterId == Guid.Empty
            || command.ExpectedCharacterRevision < 0
            || command.DestinationContainerId == Guid.Empty
            || command.ExpectedDestinationContainerRevision < 0
            || command.DestinationSlotIndex < 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "The live Mob loot grant is incomplete or invalid.");
        }

        await context.LockCharacterStatesAsync(
            [new CharacterLockRequest(
                command.CharacterId,
                command.ExpectedCharacterRevision)],
            cancellationToken);
        await context.LockMutationScopeAsync(
            [],
            [command.DestinationContainerId],
            cancellationToken);
        var definition = await context.LoadDefinitionAsync(
            command.DefinitionId,
            cancellationToken);
        ValidateMobLootDefinition(definition, command.Quantity);
        var destination = await context.LoadOwnedContainerAsync(
            command.DestinationContainerId,
            command.CharacterId,
            cancellationToken);
        if (destination.Revision != command.ExpectedDestinationContainerRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The Mob loot destination container revision is stale.");
        }

        var slotIndex = await context.SelectDestinationSlotAsync(
            destination,
            definition,
            command.DestinationSlotIndex,
            false,
            null,
            false,
            cancellationToken);
        var itemInstanceId = CreateScopedGuid("mob-loot-item", command.GrantId);
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
                command.Quantity,
                ContainerId = destination.ContainerId,
                SlotIndex = slotIndex
            },
            context.Transaction,
            cancellationToken: cancellationToken));
        var afterState = await context.CaptureItemStateJsonAsync(
            itemInstanceId,
            cancellationToken);
        context.AddItemAudit(
            "live_mob_loot_granted",
            itemInstanceId,
            null,
            afterState);
        context.AddMetadataAudit(
            "live_mob_loot_grant_committed",
            null,
            new
            {
                command.GrantId,
                command.SourceCorpseId,
                command.SourceActorDefinitionId,
                command.CharacterId,
                ItemInstanceId = itemInstanceId
            });
        context.TouchContainer(destination.ContainerId);
        context.TouchCharacter(command.CharacterId);
        context.IncludeResultItem(itemInstanceId);
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

    private static void ValidatePersistentMobCorpseCommand(
        CreatePersistentMobCorpseCommand command)
    {
        if (command.CorpseId == Guid.Empty
            || !IsMobIdentifier(command.WorkerId)
            || !IsMobIdentifier(command.WorkerRuntimeId)
            || !IsMobIdentifier(command.ShardId)
            || !IsMobIdentifier(command.SourceActorDefinitionId)
            || string.IsNullOrWhiteSpace(command.SourceDisplayName)
            || command.SourceDisplayName.Length > 128
            || !string.Equals(
                command.PresentationKey,
                PlayerCorpsePresentationKey,
                StringComparison.Ordinal)
            || !double.IsFinite(command.LifetimeSeconds)
            || command.LifetimeSeconds
                is < WorldActorCorpseRules.MinimumLifetimeSeconds
                or > WorldActorCorpseRules.MaximumLifetimeSeconds
            || command.WorkerHeartbeatTimeoutSeconds <= 0
            || !IsFinitePosition(command.PositionX)
            || !IsFinitePosition(command.PositionY)
            || !IsFinitePosition(command.PositionZ)
            || !IsNormalizedRotation(
                command.RotationX,
                command.RotationY,
                command.RotationZ,
                command.RotationW)
            || command.Loot is null
            || command.Loot.Count is < 1 or > WorldActorCorpseRules.MaximumLootEntries
            || command.Loot.Any(loot =>
                loot.GrantId == Guid.Empty
                || !IsMobIdentifier(loot.DefinitionId)
                || loot.Quantity <= 0)
            || command.Loot.Select(loot => loot.GrantId).Distinct().Count()
                != command.Loot.Count)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobCorpseInvalid,
                "The persistent Mob corpse command is incomplete or invalid.");
        }
    }

    private static void ValidateMobLootDefinition(
        ResolvedItemDefinition definition,
        int quantity)
    {
        if (quantity <= 0
            || quantity > definition.RuntimeDefinition.MaximumStackSize
            || definition.IsBag
            || definition.RuntimeDefinition.DefaultPolicies.Length > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "Mob loot must be transferable, policy-free, non-Bag item content within its stack limit.");
        }
    }

    private static bool IsMobIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    private static bool IsNormalizedRotation(
        double rotationX,
        double rotationY,
        double rotationZ,
        double rotationW)
    {
        var lengthSquared = (rotationX * rotationX)
            + (rotationY * rotationY)
            + (rotationZ * rotationZ)
            + (rotationW * rotationW);
        return double.IsFinite(lengthSquared)
            && Math.Abs(lengthSquared - 1d) <= 0.0001d
            && Math.Abs(rotationX) <= 1d
            && Math.Abs(rotationY) <= 1d
            && Math.Abs(rotationZ) <= 1d
            && Math.Abs(rotationW) <= 1d;
    }

    private static Guid CreateScopedGuid(string scope, Guid sourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            scope + ":" + sourceId.ToString("N")));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
