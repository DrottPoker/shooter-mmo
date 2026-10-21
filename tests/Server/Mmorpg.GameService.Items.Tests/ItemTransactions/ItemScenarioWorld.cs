using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Builds item scenarios through the untraced production service. Every helper asserts
// that its setup step committed, so a golden trace never starts from a broken state.
internal sealed class ItemScenarioWorld(PostgresIntegrationTestContext context)
{
    public const string WorkerId = "local-simulation-worker-1";
    public const string WorkerRuntimeId = "integration-worker-runtime";
    public const string ShardId = "local-shard-1";
    public const string CorpsePresentationKey = "corpse.remains";
    public const string MobCorpsePresentationKey = "corpse.mob";

    public PostgresIntegrationTestContext Context { get; } = context;

    public Task<IntegrationPlayer> RegisterAsync(string name)
    {
        return Context.RegisterPlayerAsync(
            $"{name}@example.com",
            name,
            name.Replace('_', ' '));
    }

    public async Task<ItemStateRow> LoadStateAsync(Guid characterId)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<ItemStateRow>(
            """
            select
                character_id as "CharacterId",
                revision as "Revision",
                permanent_inventory_container_id as "PermanentInventoryContainerId",
                bank_container_id as "BankContainerId",
                secure_container_id as "SecureContainerId",
                recovery_storage_container_id as "RecoveryStorageContainerId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId });
    }

    public async Task<ItemRow> LoadItemAsync(Guid itemInstanceId)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<ItemRow>(
            """
            select
                item.id as "ItemInstanceId",
                item.revision as "Revision",
                item.quantity as "Quantity",
                item.container_id as "ContainerId",
                item.container_slot_index as "ContainerSlotIndex",
                bag_contents.id as "BagContentsContainerId",
                bag_contents.revision as "BagContentsRevision"
            from item_instances item
            left join item_containers bag_contents
              on bag_contents.bound_bag_item_instance_id = item.id
             and bag_contents.container_type in ('bag_contents', 'corpse_bag_contents')
            where item.id = @ItemInstanceId;
            """,
            new { ItemInstanceId = itemInstanceId });
    }

    public async Task<long> LoadContainerRevisionAsync(Guid containerId)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<long>(
            "select revision from item_containers where id = @ContainerId;",
            new { ContainerId = containerId });
    }

    public async Task<CorpseSectionsRow> LoadCorpseSectionsAsync(Guid corpseId)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<CorpseSectionsRow>(
            """
            select
                corpse.id as "CorpseId",
                corpse.expiry_operation_id as "ExpiryOperationId",
                (
                    select container_id
                    from corpse_sections
                    where corpse_id = corpse.id
                      and section_kind = 'general_inventory') as "GeneralContainerId",
                (
                    select container_id
                    from corpse_sections
                    where corpse_id = corpse.id
                      and section_kind = 'equipment') as "EquipmentContainerId",
                (
                    select container_id
                    from corpse_sections
                    where corpse_id = corpse.id
                      and section_kind = 'bag') as "BagContainerId"
            from corpses corpse
            where corpse.id = @CorpseId;
            """,
            new { CorpseId = corpseId });
    }

    public async Task<Guid> FindItemAsync(Guid containerId, int slotIndex)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<Guid>(
            """
            select id
            from item_instances
            where container_id = @ContainerId
              and container_slot_index = @SlotIndex;
            """,
            new { ContainerId = containerId, SlotIndex = slotIndex });
    }

    public async Task<Guid> FindEquipmentContainerItemAsync(Guid containerId)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<Guid>(
            "select id from item_instances where container_id = @ContainerId;",
            new { ContainerId = containerId });
    }

    public async Task ExecuteSqlAsync(string sql, object? parameters = null)
    {
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    public async Task<Guid> GrantAsync(
        Guid characterId,
        string definitionId,
        int quantity,
        Guid containerId,
        int slotIndex,
        string? policySourceKind = null,
        string? policySourceId = null)
    {
        var state = await LoadStateAsync(characterId);
        var result = await Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    definitionId,
                    quantity,
                    containerId,
                    slotIndex,
                    policySourceKind,
                    policySourceId)),
            CancellationToken.None);
        AssertCommitted(result);
        return Assert.Single(result.ItemRevisions).ItemInstanceId;
    }

    public async Task EquipAsync(Guid characterId, Guid itemInstanceId, string equipmentSlotId)
    {
        var state = await LoadStateAsync(characterId);
        var item = await LoadItemAsync(itemInstanceId);
        AssertCommitted(await Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(
                    characterId,
                    state.Revision,
                    itemInstanceId,
                    item.Revision,
                    equipmentSlotId)),
            CancellationToken.None));
    }

    public async Task ApplyPolicyAsync(Guid characterId, Guid itemInstanceId, string policyKind)
    {
        var state = await LoadStateAsync(characterId);
        var item = await LoadItemAsync(itemInstanceId);
        AssertCommitted(await Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new ApplyItemPolicyCommand(
                    characterId,
                    state.Revision,
                    itemInstanceId,
                    item.Revision,
                    policyKind,
                    "integration_fixture",
                    "golden")),
            CancellationToken.None));
    }

    public async Task<Guid> AddRecoveryDeliveryAsync(
        Guid characterId,
        Guid itemInstanceId,
        DateTime? expiresAt = null)
    {
        var state = await LoadStateAsync(characterId);
        var item = await LoadItemAsync(itemInstanceId);
        var result = await Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    characterId,
                    state.Revision,
                    "golden_fixture",
                    Guid.NewGuid().ToString("N"),
                    null,
                    expiresAt,
                    [new ItemRevisionExpectation(itemInstanceId, item.Revision)])),
            CancellationToken.None);
        AssertCommitted(result);
        return Assert.Single(result.RecoveryDeliveryIds);
    }

    public async Task<Guid> ProcessDeathAsync(Guid characterId)
    {
        var state = await LoadStateAsync(characterId);
        var result = await Context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                CreateDeathCommand(characterId, state.Revision)),
            CancellationToken.None);
        AssertCommitted(result);
        await using var connection = await Context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<Guid>(
            """
            select corpse_id
            from death_events
            where operation_id = @OperationId;
            """,
            new { result.OperationId });
    }

    public static ProcessPlayerDeathCommand CreateDeathCommand(
        Guid characterId,
        long expectedCharacterRevision,
        Guid? deathEventId = null)
    {
        return new ProcessPlayerDeathCommand(
            deathEventId ?? Guid.NewGuid(),
            characterId,
            expectedCharacterRevision,
            ShardId,
            12.5d,
            -3.25d,
            0d,
            0d,
            0d,
            0d,
            1d,
            CorpsePresentationKey);
    }

    public async Task AddSecureTierAsync(string tierId, int slotCapacity)
    {
        await ExecuteSqlAsync(
            """
            insert into secure_container_tiers (
                id,
                catalog_id,
                catalog_revision,
                display_name,
                slot_capacity,
                structural_fingerprint)
            select
                @TierId,
                catalog_id,
                revision,
                'Golden Secure Container',
                @SlotCapacity,
                repeat('c', 64)
            from item_catalog_revisions
            where is_current;
            """,
            new { TierId = tierId, SlotCapacity = slotCapacity });
    }

    public async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(IntegrationPlayer player)
    {
        var join = await Context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await Context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                WorkerRuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    public static ItemTransactionActor SimulationActor(
        ConsumedSimulationJoinTicketResponse session,
        ItemTransactionLiveAccess? access = null)
    {
        return ItemTransactionActor.ForSimulationWorker(
            session.AccountId,
            session.CharacterId,
            session.SimulationSessionId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            TokenGenerator.HashToken(session.SimulationSessionToken),
            access ?? ItemTransactionLiveAccess.None);
    }

    public static void AssertCommitted(ItemTransactionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
    }

    internal sealed class ItemStateRow
    {
        public Guid CharacterId { get; set; }

        public long Revision { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid SecureContainerId { get; set; }

        public Guid RecoveryStorageContainerId { get; set; }
    }

    internal sealed class ItemRow
    {
        public Guid ItemInstanceId { get; set; }

        public long Revision { get; set; }

        public int Quantity { get; set; }

        public Guid? ContainerId { get; set; }

        public int? ContainerSlotIndex { get; set; }

        public Guid? BagContentsContainerId { get; set; }

        public long? BagContentsRevision { get; set; }
    }

    internal sealed class CorpseSectionsRow
    {
        public Guid CorpseId { get; set; }

        public Guid ExpiryOperationId { get; set; }

        public Guid GeneralContainerId { get; set; }

        public Guid EquipmentContainerId { get; set; }

        public Guid BagContainerId { get; set; }
    }
}
