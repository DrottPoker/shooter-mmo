using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Partitions a dead character's carried items into a durable corpse and Recovery
// Storage deliveries once per death event, and replays the event idempotently.
internal sealed class PlayerDeathCommandHandler
    : IItemCommandHandler<ProcessPlayerDeathCommand>
{
    private const string DeathProtectionRecoverySource = "death_protection";
    private const string ProtectedBagRecoverySource = "protected_bag";
    private const string InsuranceRecoverySource = "insurance";

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        ProcessPlayerDeathCommand command,
        CancellationToken cancellationToken)
    {
        ValidatePlayerDeathCommand(command);
        EnsureDeathAuthority(context);
        await DurableCorpseRecords.LockDeathEventKeyAsync(context, command.DeathEventId, cancellationToken);

        var characterState = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var requestHash = CalculateDeathEventRequestHash(command);
        var existingEvent = await LoadDeathEventAsync(
            context,
            command.DeathEventId,
            cancellationToken);
        if (existingEvent is not null)
        {
            if (!string.Equals(existingEvent.RequestHash, requestHash, StringComparison.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.DeathEventConflict,
                    "The death event id was already used with different authoritative death data.");
            }

            context.Journal.AddMetadataAudit(
                "death_event_replayed",
                new
                {
                    command.DeathEventId,
                    existingEvent.CorpseId,
                    existingEvent.OriginalOperationId
                },
                new
                {
                    command.DeathEventId,
                    existingEvent.CorpseId,
                    existingEvent.OriginalOperationId
                });
            return;
        }

        // The death event row is removed with its closed corpse after audit retention.
        // The partition always advances the revision, so a replay that outlives the
        // event row is still rejected here instead of partitioning a second time.
        if (characterState.Revision != command.ExpectedCharacterRevision)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemStateConflict,
                "The character item state changed before the death event committed.");
        }

        context.Journal.AllowInvoluntaryDeathHardCapOverflow();

        var character = await context.Connection.QuerySingleOrDefaultAsync<CharacterDeathSourceRow>(
            new CommandDefinition(
                $"""
                select
                    character.name as "DisplayName",
                    {PlacementSql.ShardExists("@ShardId")} as "ShardExists"
                from {CharactersSql.ActiveCharacters} character
                where character.id = @CharacterId;
                """,
                new { command.CharacterId, command.ShardId },
                context.Transaction,
                cancellationToken: cancellationToken));
        if (character is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.ItemNotFound,
                "The death event character was not found.");
        }

        if (!character.ShardExists)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The death event Shard was not found.");
        }

        var corpseId = Guid.NewGuid();
        var expiryOperationId = Guid.NewGuid();
        var corpseTimes = await DurableCorpseRecords.CreateDurableCorpseRecordAsync(
            context,
            corpseId,
            "player",
            command.CharacterId,
            null,
            character.DisplayName,
            command.ShardId,
            command.PositionX,
            command.PositionY,
            command.PositionZ,
            command.RotationX,
            command.RotationY,
            command.RotationZ,
            command.RotationW,
            command.PresentationKey,
            0,
            TimeSpan.FromMinutes(5).TotalSeconds,
            expiryOperationId,
            cancellationToken);

        var sourceRows = (await context.Connection.QueryAsync<DeathItemSourceRow>(
            new CommandDefinition(
                """
                select
                    item.id as "ItemInstanceId",
                    item.container_id as "ContainerId",
                    item.container_slot_index as "ContainerSlotIndex",
                    item.equipment_slot_id as "EquipmentSlotId",
                    equipment.corpse_slot_index as "EquipmentCorpseSlotIndex",
                    bag_container.id as "BagContentsContainerId",
                    case
                        when item.equipped_character_id = @CharacterId then 'equipment'
                        when item.container_id = @PermanentInventoryContainerId then 'permanent_inventory'
                        else 'bag_contents'
                    end as "SourceKind"
                from (
                    -- Each branch is an indexed lookup instead of one OR over joins.
                    select inventory_item.id
                    from item_instances inventory_item
                    where inventory_item.container_id = @PermanentInventoryContainerId
                    union
                    select equipped_item.id
                    from item_instances equipped_item
                    where equipped_item.equipped_character_id = @CharacterId
                    union
                    select bag_item.id
                    from item_instances equipped_bag
                    join item_containers bag_contents
                      on bag_contents.bound_bag_item_instance_id = equipped_bag.id
                    join item_instances bag_item on bag_item.container_id = bag_contents.id
                    where equipped_bag.equipped_character_id = @CharacterId
                      and equipped_bag.equipment_slot_id = 'bag'
                      and bag_contents.container_type = 'bag_contents'
                      and bag_contents.lifecycle = 'active'
                ) source_item
                join item_instances item on item.id = source_item.id
                left join item_containers bag_container
                  on bag_container.bound_bag_item_instance_id = item.id
                 and bag_container.container_type = 'bag_contents'
                -- The corpse equipment section lists the active slots in display order.
                left join (
                    select
                        id,
                        (row_number() over (order by display_order, id) - 1)::integer
                            as corpse_slot_index
                    from equipment_slots
                    where is_active
                ) equipment on equipment.id = item.equipment_slot_id
                order by item.id;
                """,
                new
                {
                    command.CharacterId,
                    characterState.PermanentInventoryContainerId
                },
                context.Transaction,
                cancellationToken: cancellationToken))).ToArray();
        var sourceContainerIds = sourceRows
            .Select(row => row.ContainerId)
            .Where(containerId => containerId is not null)
            .Select(containerId => containerId!.Value)
            .Append(characterState.PermanentInventoryContainerId)
            .Append(characterState.RecoveryStorageContainerId)
            .Distinct()
            .Order()
            .ToArray();
        await context.Locks.LockMutationScopeAsync(
            sourceRows.Select(row => row.ItemInstanceId),
            sourceContainerIds,
            cancellationToken);

        var partitionItems = new List<DeathPartitionItem>(sourceRows.Length);
        foreach (var source in sourceRows)
        {
            var item = await context.State.LoadItemAsync(source.ItemInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Death item '{source.ItemInstanceId}' disappeared after locking.");
            var definition = await context.State.LoadDefinitionAsync(
                item.DefinitionId,
                cancellationToken);
            var policies = await context.State.LoadPoliciesAsync(
                item.ItemInstanceId,
                cancellationToken);
            var capabilities = ItemPolicyRules.Evaluate(
                definition.RuntimeDefinition,
                ItemPolicyStates.ToPolicyStates(policies));
            partitionItems.Add(new DeathPartitionItem(
                source,
                item,
                definition,
                capabilities.DeathDisposition,
                await context.Journal.CaptureItemStateJsonAsync(item.ItemInstanceId, cancellationToken)));
        }

        var equippedBag = partitionItems.SingleOrDefault(item => item.IsEquippedBag);
        var generalContainerId = Guid.NewGuid();
        var equipmentContainerId = Guid.NewGuid();
        var useLiveBagAggregate = equippedBag is not null
            && equippedBag.Disposition == ItemDeathDisposition.Lootable;
        var bagContainerId = useLiveBagAggregate
            ? equippedBag!.Item.BagContentsContainerId
                ?? throw new InvalidOperationException("The equipped Bag has no content container.")
            : Guid.NewGuid();

        await PlayerCorpseLayoutWriter.CreateCorpseSectionContainersAsync(
            context,
            corpseId,
            characterState.PermanentInventoryContainerId,
            generalContainerId,
            equipmentContainerId,
            bagContainerId,
            useLiveBagAggregate,
            equippedBag,
            cancellationToken);

        await PlayerCorpseLayoutWriter.CreateCorpseSnapshotsAsync(
            context,
            corpseId,
            characterState,
            partitionItems,
            equippedBag,
            cancellationToken);

        foreach (var item in partitionItems
                     .Where(item => item.Disposition == ItemDeathDisposition.Lootable)
                     .OrderBy(item => item.Item.ItemInstanceId))
        {
            await DeathCustodyTransfers.MoveLootableItemToCorpseAsync(
                context,
                item,
                generalContainerId,
                equipmentContainerId,
                bagContainerId,
                useLiveBagAggregate,
                cancellationToken);
        }

        await DeathCustodyTransfers.AddDeathRecoveryDeliveriesAsync(
            context,
            command.CharacterId,
            characterState.RecoveryStorageContainerId,
            command.DeathEventId,
            [
                new DeathRecoveryGroup(
                    DeathProtectionRecoverySource,
                    false,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.ProtectedRecovery
                            && !item.Definition.IsBag)
                        .ToArray()),
                new DeathRecoveryGroup(
                    ProtectedBagRecoverySource,
                    false,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.ProtectedRecovery
                            && item.Definition.IsBag)
                        .ToArray()),
                new DeathRecoveryGroup(
                    InsuranceRecoverySource,
                    true,
                    partitionItems.Where(item =>
                            item.Disposition == ItemDeathDisposition.InsuredRecovery)
                        .ToArray())
            ],
            cancellationToken);

        await context.Connection.ExecuteAsync(new CommandDefinition(
            """
            update corpses
            set revision = revision + 1
            where id = @CorpseId;

            insert into death_events (
                id,
                character_id,
                corpse_id,
                operation_id,
                request_hash,
                result_payload)
            values (
                @DeathEventId,
                @CharacterId,
                @CorpseId,
                @OperationId,
                @RequestHash,
                cast(@ResultPayload as jsonb));
            """,
            new
            {
                DeathEventId = command.DeathEventId,
                command.CharacterId,
                CorpseId = corpseId,
                context.OperationId,
                RequestHash = requestHash,
                ResultPayload = JsonSerializer.Serialize(
                    new
                    {
                        corpseId,
                        expiryOperationId,
                        corpseTimes.CreatedAt,
                        corpseTimes.ExpiresAt
                    },
                    ItemOperationJson.Options)
            },
            context.Transaction,
            cancellationToken: cancellationToken));

        context.Journal.TouchContainer(characterState.PermanentInventoryContainerId);
        context.Journal.TouchContainer(generalContainerId);
        context.Journal.TouchContainer(equipmentContainerId);
        context.Journal.TouchContainer(bagContainerId);
        context.Journal.TouchCharacter(command.CharacterId);
        context.Journal.AddMetadataAudit(
            "player_death_partitioned",
            new
            {
                command.CharacterId,
                PreviousCharacterRevision = characterState.Revision,
                ItemCount = partitionItems.Count
            },
            new
            {
                command.DeathEventId,
                CorpseId = corpseId,
                ExpiresAt = corpseTimes.ExpiresAt,
                LootableItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.Lootable),
                ProtectedItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.ProtectedRecovery),
                InsuredItemCount = partitionItems.Count(item =>
                    item.Disposition == ItemDeathDisposition.InsuredRecovery)
            });
    }

    private static Task<DeathEventRow?> LoadDeathEventAsync(
        ItemTransactionContext context,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        return context.Connection.QuerySingleOrDefaultAsync<DeathEventRow>(new CommandDefinition(
            """
            select
                event.request_hash as "RequestHash",
                event.corpse_id as "CorpseId",
                event.operation_id as "OriginalOperationId"
            from death_events event
            join corpses corpse on corpse.id = event.corpse_id
            where event.id = @DeathEventId
            for update of event, corpse;
            """,
            new { DeathEventId = deathEventId },
            context.Transaction,
            cancellationToken: cancellationToken));
    }

    internal static string CalculateDeathEventRequestHash(ProcessPlayerDeathCommand command)
    {
        var canonical = JsonSerializer.Serialize(
            new DeathEventRequestFingerprint(
                command.DeathEventId,
                command.CharacterId,
                command.ShardId,
                command.PositionX,
                command.PositionY,
                command.PositionZ,
                command.RotationX,
                command.RotationY,
                command.RotationZ,
                command.RotationW,
                command.PresentationKey),
            ItemOperationJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void EnsureDeathAuthority(ItemTransactionContext context)
    {
        if (context.Actor.Authority is not ItemTransactionAuthority.System
            and not ItemTransactionAuthority.SimulationWorker)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "Player death partition requires system or simulation authority.");
        }
    }

    internal static void ValidatePlayerDeathCommand(ProcessPlayerDeathCommand command)
    {
        if (command.DeathEventId == Guid.Empty || command.CharacterId == Guid.Empty)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "A death event and character id are required.");
        }

        if (!IsDeathIdentifier(command.ShardId))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationSessionInvalid,
                "The death event Shard id is invalid.");
        }

        if (!IsPresentationKey(command.PresentationKey)
            || !string.Equals(
                command.PresentationKey,
                CorpseTransformRules.PlayerCorpsePresentationKey,
                StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "Player death must use the authoritative generic corpse presentation key.");
        }

        if (!CorpseTransformRules.IsFinitePosition(command.PositionX)
            || !CorpseTransformRules.IsFinitePosition(command.PositionY)
            || !CorpseTransformRules.IsFinitePosition(command.PositionZ))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "The death transform position is invalid.");
        }

        var rotationLengthSquared = (command.RotationX * command.RotationX)
            + (command.RotationY * command.RotationY)
            + (command.RotationZ * command.RotationZ)
            + (command.RotationW * command.RotationW);
        if (!double.IsFinite(rotationLengthSquared)
            || Math.Abs(rotationLengthSquared - 1d) > 0.0001d
            || Math.Abs(command.RotationX) > 1d
            || Math.Abs(command.RotationY) > 1d
            || Math.Abs(command.RotationZ) > 1d
            || Math.Abs(command.RotationW) > 1d)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.DeathEventConflict,
                "The death transform rotation must be a normalized quaternion.");
        }
    }

    private static bool IsDeathIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsPresentationKey(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private sealed record DeathEventRequestFingerprint(
        Guid DeathEventId,
        Guid CharacterId,
        string ShardId,
        double PositionX,
        double PositionY,
        double PositionZ,
        double RotationX,
        double RotationY,
        double RotationZ,
        double RotationW,
        string PresentationKey);

    private sealed class CharacterDeathSourceRow
    {
        public string DisplayName { get; set; } = string.Empty;

        public bool ShardExists { get; set; }
    }

    private sealed class DeathEventRow
    {
        public string RequestHash { get; set; } = string.Empty;

        public Guid CorpseId { get; set; }

        public Guid OriginalOperationId { get; set; }
    }
}
