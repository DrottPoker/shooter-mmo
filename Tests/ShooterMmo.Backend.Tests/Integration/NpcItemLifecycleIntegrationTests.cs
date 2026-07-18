using AuthService.Items;
using AuthService.Simulation;
using Dapper;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class NpcItemLifecycleIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task InsuranceNpcChargesOnceBlocksTransferAndRemovalPreservesItemIdentity()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "npc-insurance@example.com",
            "npc_insurance_player",
            "NPC Insurance Hero");
        await SetCurrencyAsync(context, player.Character.Id, 250);
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "weapon.training_rifle",
            snapshot.PermanentInventory.ContainerId,
            0);
        var item = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);
        var interactionSessionId = Guid.NewGuid();

        var applied = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: item.ItemInstanceId,
                itemRevision: item.Revision));
        Assert.True(applied.Succeeded, applied.Error?.Message);
        item = Assert.Single(applied.Value!.ItemRevisions);
        var characterRevision = Assert.Single(applied.Value.CharacterRevisions).Revision;
        Assert.Equal(150, await GetCurrencyAsync(context, player.Character.Id));

        var afterApply = await GetSnapshotAsync(context, player);
        var insuredItem = FindItem(afterApply, item.ItemInstanceId);
        var policy = Assert.Single(insuredItem.Policies);
        Assert.Equal(ItemPolicyIds.Insured, policy.PolicyKind);
        Assert.Equal("insurance_npc", policy.ProtectionSource);
        var definition = await LoadDefinitionAsync(context, insuredItem.DefinitionId);
        var insuredCapabilities = ItemPolicyRules.Evaluate(
            definition,
            [new ItemPolicyState(
                policy.PolicyKind,
                policy.Status,
                ItemPolicySourceKinds.InsuranceService,
                "npc")]);
        Assert.False(insuredCapabilities.CanChangeOwningCharacter);
        Assert.False(insuredCapabilities.CanTrade);
        Assert.False(insuredCapabilities.CanListOnAuction);
        Assert.False(insuredCapabilities.CanSellToVendor);

        var duplicate = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                characterRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: item.ItemInstanceId,
                itemRevision: item.Revision));
        Assert.False(duplicate.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, duplicate.Error!.Code);
        Assert.Equal(150, await GetCurrencyAsync(context, player.Character.Id));

        var removed = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.RemoveInsurancePolicy,
                characterRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: item.ItemInstanceId,
                itemRevision: item.Revision));
        Assert.True(removed.Succeeded, removed.Error?.Message);
        var removedItem = Assert.Single(removed.Value!.ItemRevisions);
        Assert.Equal(item.ItemInstanceId, removedItem.ItemInstanceId);
        var afterRemoval = await GetSnapshotAsync(context, player);
        Assert.Empty(FindItem(afterRemoval, item.ItemInstanceId).Policies);
        var normalCapabilities = ItemPolicyRules.Evaluate(definition, []);
        Assert.True(normalCapabilities.CanChangeOwningCharacter);
        Assert.True(normalCapabilities.CanTrade);
        Assert.True(normalCapabilities.CanListOnAuction);
        Assert.True(normalCapabilities.CanSellToVendor);
    }

    [PostgresIntegrationFact]
    public async Task InsuranceNpcValidatesAccessEligibilityOwnershipAndPrice()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "npc-insurance-fences@example.com",
            "npc_insurance_fences",
            "Insurance Fence Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "ammunition.training_556",
            snapshot.PermanentInventory.ContainerId,
            0,
            10);
        var ammunition = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var rifleGrant = await GrantAsync(
            context,
            player,
            snapshot,
            "weapon.training_rifle",
            snapshot.PermanentInventory.ContainerId,
            1);
        var rifle = Assert.Single(rifleGrant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var otherPlayer = await context.RegisterPlayerAsync(
            "npc-insurance-owner@example.com",
            "npc_insurance_owner",
            "Insurance Owner Hero");
        var otherSnapshot = await GetSnapshotAsync(context, otherPlayer);
        var otherGrant = await GrantAsync(
            context,
            otherPlayer,
            otherSnapshot,
            "weapon.training_rifle",
            otherSnapshot.PermanentInventory.ContainerId,
            0);
        var otherRifle = Assert.Single(otherGrant.ItemRevisions);
        var session = await JoinAsync(context, player);
        var interactionSessionId = Guid.NewGuid();

        var noAccess = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: false,
                itemInstanceId: ammunition.ItemInstanceId,
                itemRevision: ammunition.Revision));
        Assert.False(noAccess.Succeeded);

        var ineligible = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: ammunition.ItemInstanceId,
                itemRevision: ammunition.Revision));
        Assert.False(ineligible.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, ineligible.Error!.Code);

        var missing = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: Guid.NewGuid(),
                itemRevision: 0));
        Assert.False(missing.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, missing.Error!.Code);

        var wrongOwner = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: otherRifle.ItemInstanceId,
                itemRevision: otherRifle.Revision));
        Assert.False(wrongOwner.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotOwned, wrongOwner.Error!.Code);

        var insufficient = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.ApplyItemPolicy,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.insurance",
                insuranceAccess: true,
                itemInstanceId: rifle.ItemInstanceId,
                itemRevision: rifle.Revision));
        Assert.False(insufficient.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CurrencyInsufficient, insufficient.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task QuestNpcAcceptAbandonAndReacceptUseOneExactProtectedGrantLineage()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "npc-quest@example.com",
            "npc_quest_player",
            "NPC Quest Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);
        var interactionSessionId = Guid.NewGuid();

        var accepted = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.Grant,
                snapshot.ItemStateRevision,
                interactionSessionId,
                "services.quest_offer",
                questAccess: true,
                destinationContainerId: snapshot.PermanentInventory.ContainerId));
        Assert.True(accepted.Succeeded, accepted.Error?.Message);
        var questItem = Assert.Single(accepted.Value!.ItemRevisions);
        var currentRevision = Assert.Single(accepted.Value.CharacterRevisions).Revision;
        Assert.Equal(1, await CountQuestGrantItemsAsync(context));

        var duplicate = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.Grant,
                currentRevision,
                interactionSessionId,
                "services.quest_offer",
                questAccess: true,
                destinationContainerId: snapshot.PermanentInventory.ContainerId));
        Assert.True(duplicate.Succeeded, duplicate.Error?.Message);
        Assert.Equal(questItem.ItemInstanceId, Assert.Single(duplicate.Value!.ItemRevisions).ItemInstanceId);
        Assert.Equal(1, await CountQuestGrantItemsAsync(context));

        var directDestroy = await ExecuteAsync(
            context,
            session,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                ItemOperationKinds.Destroy,
                currentRevision,
                questItem.ItemInstanceId,
                questItem.Revision));
        Assert.False(directDestroy.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, directDestroy.Error!.Code);

        var abandoned = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.AbandonQuestItems,
                currentRevision,
                interactionSessionId,
                "services.quest_offer",
                questAccess: true));
        Assert.True(abandoned.Succeeded, abandoned.Error?.Message);
        currentRevision = Assert.Single(abandoned.Value!.CharacterRevisions).Revision;
        Assert.Equal(0, await CountQuestGrantItemsAsync(context));

        var reaccepted = await ExecuteAsync(
            context,
            session,
            CreateNpcRequest(
                session,
                ItemOperationKinds.Grant,
                currentRevision,
                interactionSessionId,
                "services.quest_offer",
                questAccess: true,
                destinationContainerId: snapshot.PermanentInventory.ContainerId));
        Assert.True(reaccepted.Succeeded, reaccepted.Error?.Message);
        Assert.NotEqual(questItem.ItemInstanceId, Assert.Single(reaccepted.Value!.ItemRevisions).ItemInstanceId);
        Assert.Equal(1, await CountQuestGrantItemsAsync(context));
    }

    private static SimulationItemOperationRequest CreateNpcRequest(
        ConsumedSimulationJoinTicketResponse session,
        string operationKind,
        long characterRevision,
        Guid interactionSessionId,
        string capabilityId,
        bool insuranceAccess = false,
        bool questAccess = false,
        Guid itemInstanceId = default,
        long itemRevision = 0,
        Guid destinationContainerId = default)
    {
        return new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(
                false,
                false,
                insuranceAccess,
                questAccess),
            operationKind,
            characterRevision,
            ItemInstanceId: itemInstanceId == Guid.Empty ? null : itemInstanceId,
            ExpectedItemRevision: itemInstanceId == Guid.Empty ? null : itemRevision,
            DestinationContainerId: destinationContainerId == Guid.Empty
                ? null
                : destinationContainerId,
            InteractionSessionId: interactionSessionId,
            CapabilityId: capabilityId);
    }

    private static Task<AuthService.Http.ServiceResult<ItemTransactionResult>> ExecuteAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        SimulationItemOperationRequest request)
    {
        return context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
    }

    private static async Task<ItemTransactionResult> GrantAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        CharacterInventorySnapshotResponse snapshot,
        string definitionId,
        Guid destinationContainerId,
        int destinationSlotIndex,
        int quantity = 1)
    {
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    definitionId,
                    quantity,
                    destinationContainerId,
                    destinationSlotIndex)),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result;
    }

    private static async Task<CharacterInventorySnapshotResponse> GetSnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static ItemInstanceSnapshotResponse FindItem(
        CharacterInventorySnapshotResponse snapshot,
        Guid itemId)
    {
        return snapshot.PermanentInventory.Slots
            .Concat(snapshot.Bank.Slots)
            .Concat(snapshot.SecureContainer.Contents.Slots)
            .Select(slot => slot.Item)
            .Concat(snapshot.Equipment.Select(slot => slot.Item))
            .Where(item => item is not null)
            .Single(item => item!.ItemInstanceId == itemId)!;
    }

    private static async Task<ItemDefinition> LoadDefinitionAsync(
        PostgresIntegrationTestContext context,
        string definitionId)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);
        return catalog.Definitions.Single(definition => definition.Id == definitionId);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                RuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static async Task SetCurrencyAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        long currency)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "update characters set currency = @Currency where id = @CharacterId;",
            new { CharacterId = characterId, Currency = currency });
    }

    private static async Task<long> GetCurrencyAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<long>(
            "select currency from characters where id = @CharacterId;",
            new { CharacterId = characterId });
    }

    private static async Task<int> CountQuestGrantItemsAsync(
        PostgresIntegrationTestContext context)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select count(*)
            from item_instance_policies
            where policy_kind = 'protected_on_death'
              and status = 'active'
              and source_kind = @SourceKind
              and source_id = @SourceId;
            """,
            new
            {
                SourceKind = ItemPolicySourceKinds.QuestGrant,
                SourceId = NpcItemLifecycleOptions.Default.QuestGrantId
            });
    }
}
