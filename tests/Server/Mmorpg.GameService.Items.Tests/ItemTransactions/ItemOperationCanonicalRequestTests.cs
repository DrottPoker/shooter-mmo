using System.Security.Cryptography;
using System.Text;
using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using static Mmorpg.GameService.Items.Tests.ItemTransactions.ItemScenarioWorld;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// Pins the persisted idempotency format: the canonical request payload whose SHA-256 is
// stored as item_operations.request_hash, and the death event fingerprint stored as
// death_events.request_hash. The expected JSON is written by hand, so a change in
// property names, order, sorting, or number and date formatting fails here.
public sealed class ItemOperationCanonicalRequestTests
{
    private const string SystemActor =
        """{"authority":2,"accountId":null,"requiresOfflineCharacter":false,"simulationSessionId":null,"characterId":null,"workerId":null,"workerRuntimeId":null,"shardId":null}""";

    private static readonly Guid A = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid C = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    private static readonly Guid D = Guid.Parse("a0000000-0000-4000-8000-000000000004");
    private static readonly Guid E = Guid.Parse("a0000000-0000-4000-8000-000000000005");

    [PostgresIntegrationFact]
    public async Task StoredRejectionsKeepTheCanonicalRequestHashOfEveryNonRetriedKind()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var service = context.ItemTransactionService;

        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new GrantItemCommand(A, 3, "fixture.ore", 2, B, 4)),
                CancellationToken.None),
            ItemOperationKinds.Grant,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":3,"definitionId":"fixture.ore","quantity":2,"destinationContainerId":"{{B}}","destinationSlotIndex":4,"policySourceKind":null,"policySourceId":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new RelocateItemCommand(A, null, B, 5, C, null)),
                CancellationToken.None),
            ItemOperationKinds.Relocate,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":null,"itemInstanceId":"{{B}}","expectedItemRevision":5,"destinationContainerId":"{{C}}","destinationSlotIndex":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new EquipItemCommand(A, 1, B, 2, "ring_1")),
                CancellationToken.None),
            ItemOperationKinds.Equip,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"equipmentSlotId":"ring_1"}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new UnequipItemCommand(A, 1, B, 2, C, 7)),
                CancellationToken.None),
            ItemOperationKinds.Unequip,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"destinationContainerId":"{{C}}","destinationSlotIndex":7}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new SplitItemStackCommand(A, 1, B, 2, 3, C, null)),
                CancellationToken.None),
            ItemOperationKinds.SplitStack,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"quantity":3,"destinationContainerId":"{{C}}","destinationSlotIndex":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new MergeItemStacksCommand(A, 1, B, 2, C, 3)),
                CancellationToken.None),
            ItemOperationKinds.MergeStacks,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"sourceItemInstanceId":"{{B}}","expectedSourceItemRevision":2,"targetItemInstanceId":"{{C}}","expectedTargetItemRevision":3}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new SwapContainerItemsCommand(A, 1, B, 2, C, 3)),
                CancellationToken.None),
            ItemOperationKinds.SwapContainerItems,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"firstItemInstanceId":"{{B}}","expectedFirstItemRevision":2,"secondItemInstanceId":"{{C}}","expectedSecondItemRevision":3}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new ConsumeItemQuantityCommand(A, 1, B, 2, 3)),
                CancellationToken.None),
            ItemOperationKinds.ConsumeQuantity,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"quantity":3}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new DestroyItemCommand(A, 1, B, 2, "salvage")),
                CancellationToken.None),
            ItemOperationKinds.Destroy,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"reason":"salvage"}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new SwapBagAggregatesCommand(A, 1, B, 2, 3, C, null, D, 4, 5)),
                CancellationToken.None),
            ItemOperationKinds.SwapBagAggregates,
            $$"""{"firstCharacterId":"{{A}}","expectedFirstCharacterRevision":1,"firstBagItemInstanceId":"{{B}}","expectedFirstBagRevision":2,"expectedFirstBagContentsRevision":3,"secondCharacterId":"{{C}}","expectedSecondCharacterRevision":null,"secondBagItemInstanceId":"{{D}}","expectedSecondBagRevision":4,"expectedSecondBagContentsRevision":5}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new AddRecoveryDeliveryCommand(
                    A,
                    1,
                    "canonical_source",
                    "event-7",
                    new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    new DateTime(2030, 1, 3, 0, 0, 0, 500, DateTimeKind.Utc),
                    [new ItemRevisionExpectation(C, 2), new ItemRevisionExpectation(B, 1)])),
                CancellationToken.None),
            ItemOperationKinds.AddRecoveryDelivery,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"sourceKind":"canonical_source","sourceEventId":"event-7","availableAt":"2030-01-02T03:04:05Z","expiresAt":"2030-01-03T00:00:00.5Z","items":[{"itemInstanceId":"{{C}}","revision":2},{"itemInstanceId":"{{B}}","revision":1}]}""");

        // Claim items and tier character revisions are sorted before hashing.
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new ClaimRecoveryDeliveryCommand(
                    A,
                    1,
                    B,
                    2,
                    C,
                    [new ItemRevisionExpectation(E, 4), new ItemRevisionExpectation(D, 3)])),
                CancellationToken.None),
            ItemOperationKinds.ClaimRecoveryDelivery,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"recoveryDeliveryId":"{{B}}","expectedRecoveryDeliveryRevision":2,"destinationContainerId":"{{C}}","items":[{"itemInstanceId":"{{D}}","revision":3},{"itemInstanceId":"{{E}}","revision":4}]}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new ChangeSecureContainerTierCommand(
                    A,
                    6,
                    "secure_container.base",
                    [new CharacterRevisionExpectation(C, 9), new CharacterRevisionExpectation(B, 8)])),
                CancellationToken.None),
            ItemOperationKinds.ChangeSecureContainerTier,
            $$"""{"accountId":"{{A}}","expectedEntitlementRevision":6,"tierId":"secure_container.base","characterRevisions":[{"characterId":"{{B}}","revision":8},{"characterId":"{{C}}","revision":9}]}""",
            expectedActorCharacterId: null);
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new ApplyItemPolicyCommand(A, 1, B, 2, "insured", "insurance_service", "npc", 25)),
                CancellationToken.None),
            ItemOperationKinds.ApplyItemPolicy,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2,"policyKind":"insured","sourceKind":"insurance_service","sourceId":"npc","price":25}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new RemoveInsurancePolicyCommand(A, 1, B, 2)),
                CancellationToken.None),
            ItemOperationKinds.RemoveInsurancePolicy,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"itemInstanceId":"{{B}}","expectedItemRevision":2}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new AbandonQuestItemsCommand(A, 1, "quest-9")),
                CancellationToken.None),
            ItemOperationKinds.AbandonQuestItems,
            $$"""{"characterId":"{{A}}","expectedCharacterRevision":1,"questGrantId":"quest-9"}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new LootCorpseItemCommand(A, B, C, 1, D, 2, 3, null, null)),
                CancellationToken.None),
            ItemOperationKinds.LootCorpseItem,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":null,"expectedTargetItemRevision":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new LootCorpsePartialStackCommand(A, B, C, 1, 4, D, 2, 3, E, 5)),
                CancellationToken.None),
            ItemOperationKinds.LootCorpsePartialStack,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"quantity":4,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":"{{E}}","expectedTargetItemRevision":5}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new DepositCorpseItemCommand(A, B, C, 1, D, 2, 3, null, null)),
                CancellationToken.None),
            ItemOperationKinds.DepositCorpseItem,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":null,"expectedTargetItemRevision":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new DepositCorpsePartialStackCommand(A, B, C, 1, 4, D, 2, 3, null, null)),
                CancellationToken.None),
            ItemOperationKinds.DepositCorpsePartialStack,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"quantity":4,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":null,"expectedTargetItemRevision":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new MoveCorpseItemCommand(A, B, C, 1, D, 2, 3, E, 6)),
                CancellationToken.None),
            ItemOperationKinds.MoveCorpseItem,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":"{{E}}","expectedTargetItemRevision":6}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new MoveCorpsePartialStackCommand(A, B, C, 1, 4, D, 2, 3, null, null)),
                CancellationToken.None),
            ItemOperationKinds.MoveCorpsePartialStack,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","itemInstanceId":"{{C}}","expectedItemRevision":1,"quantity":4,"destinationContainerId":"{{D}}","expectedDestinationContainerRevision":2,"destinationSlotIndex":3,"targetItemInstanceId":null,"expectedTargetItemRevision":null}""");
        await AssertStoredAsync(
            context,
            await service.ExecuteAsync(
                Request(new SwapCorpseBagCommand(A, B, C, 1, D, 2, E, 3, Guid.Parse("a0000000-0000-4000-8000-000000000006"), 4)),
                CancellationToken.None),
            ItemOperationKinds.SwapCorpseBag,
            $$"""{"characterId":"{{A}}","corpseId":"{{B}}","corpseBagItemInstanceId":"{{C}}","expectedCorpseBagRevision":1,"corpseBagContentsContainerId":"{{D}}","expectedCorpseBagContentsRevision":2,"playerBagItemInstanceId":"{{E}}","expectedPlayerBagRevision":3,"playerBagContentsContainerId":"a0000000-0000-4000-8000-000000000006","expectedPlayerBagContentsRevision":4}""");
    }

    [PostgresIntegrationFact]
    public async Task OperationClaimsRecordTheActorAccountAndExistingCharacter()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("canonical_actor");
        var characterId = player.Character.Id;
        var accountId = player.Registration.AccountId;
        var state = await world.LoadStateAsync(characterId);

        var operationId = Guid.NewGuid();
        var result = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                operationId,
                ItemTransactionActor.ForOfflineAccount(accountId),
                new RelocateItemCommand(characterId, state.Revision, A, 0, state.PermanentInventoryContainerId, 1)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, result.Error!.Code);
        var row = await LoadOperationAsync(context, operationId);
        Assert.Equal(ItemOperationKinds.Relocate, row.OperationKind);
        Assert.Equal(accountId, row.ActorAccountId);
        Assert.Equal(characterId, row.ActorCharacterId);
        Assert.Equal(
            Hash(Canonical(
                $$"""{"authority":1,"accountId":"{{accountId}}","requiresOfflineCharacter":true,"simulationSessionId":null,"characterId":null,"workerId":null,"workerRuntimeId":null,"shardId":null}""",
                $$"""{"characterId":"{{characterId}}","expectedCharacterRevision":{{state.Revision}},"itemInstanceId":"{{A}}","expectedItemRevision":0,"destinationContainerId":"{{state.PermanentInventoryContainerId}}","destinationSlotIndex":1}""")),
            row.RequestHash);
    }

    [PostgresIntegrationFact]
    public async Task CommittedRetriedSystemOperationsKeepTheirCanonicalRequestHash()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var service = context.ItemTransactionService;
        var victim = await world.RegisterAsync("canonical_victim");
        var looter = await world.RegisterAsync("canonical_looter");

        // Player death, with the death event fingerprint stored in death_events.
        var victimState = await world.LoadStateAsync(victim.Character.Id);
        var deathEventId = Guid.NewGuid();
        var deathOperationId = Guid.NewGuid();
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<ProcessPlayerDeathCommand>(
                deathOperationId,
                ItemTransactionActor.ForSystem(),
                new ProcessPlayerDeathCommand(
                    deathEventId,
                    victim.Character.Id,
                    victimState.Revision,
                    ShardId,
                    12.5d,
                    -3.25d,
                    0d,
                    0d,
                    0.6d,
                    0d,
                    0.8d,
                    CorpsePresentationKey)),
            CancellationToken.None));
        var deathFingerprint =
            $$"""{"deathEventId":"{{deathEventId}}","characterId":"{{victim.Character.Id}}","shardId":"local-shard-1","positionX":12.5,"positionY":-3.25,"positionZ":0,"rotationX":0,"rotationY":0.6,"rotationZ":0,"rotationW":0.8,"presentationKey":"corpse.remains"}""";
        await AssertOperationAsync(
            context,
            deathOperationId,
            ItemOperationKinds.ProcessPlayerDeath,
            Canonical(
                SystemActor,
                $$"""{"deathEventId":"{{deathEventId}}","characterId":"{{victim.Character.Id}}","expectedCharacterRevision":{{victimState.Revision}},"shardId":"local-shard-1","positionX":12.5,"positionY":-3.25,"positionZ":0,"rotationX":0,"rotationY":0.6,"rotationZ":0,"rotationW":0.8,"presentationKey":"corpse.remains"}"""),
            null,
            victim.Character.Id);
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                Hash(deathFingerprint),
                await connection.QuerySingleAsync<string>(
                    "select request_hash from death_events where id = @DeathEventId;",
                    new { DeathEventId = deathEventId }));
        }

        // Corpse expiry with its durable expiry operation id.
        var corpseId = await LoadCorpseIdAsync(context, deathEventId);
        var sections = await world.LoadCorpseSectionsAsync(corpseId);
        await world.ExecuteSqlAsync(
            """
            update corpses
            set created_at = now() - interval '6 minutes',
                expires_at = now() - interval '1 minute'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<ExpireCorpseCommand>(
                sections.ExpiryOperationId,
                ItemTransactionActor.ForSystem(),
                new ExpireCorpseCommand(corpseId)),
            CancellationToken.None));
        await AssertOperationAsync(
            context,
            sections.ExpiryOperationId,
            ItemOperationKinds.ExpireCorpse,
            Canonical(SystemActor, $$"""{"corpseId":"{{corpseId}}"}"""),
            null,
            null);

        // Persistent Mob corpse creation.
        var mobCorpseId = Guid.NewGuid();
        var lootGrantId = Guid.NewGuid();
        var mobOperationId = Guid.NewGuid();
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<CreatePersistentMobCorpseCommand>(
                mobOperationId,
                ItemTransactionActor.ForSystem(),
                new CreatePersistentMobCorpseCommand(
                    mobCorpseId,
                    WorkerId,
                    WorkerRuntimeId,
                    ShardId,
                    "mob.feral_wolf",
                    "Feral Wolf",
                    1.5d,
                    0d,
                    -2d,
                    0d,
                    0d,
                    0d,
                    1d,
                    CorpsePresentationKey,
                    90.25d,
                    30,
                    [new MobCorpseLootEntryRequest(lootGrantId, "fixture.ore", 3)])),
            CancellationToken.None));
        await AssertOperationAsync(
            context,
            mobOperationId,
            ItemOperationKinds.CreatePersistentMobCorpse,
            Canonical(
                SystemActor,
                $$"""{"corpseId":"{{mobCorpseId}}","workerId":"local-simulation-worker-1","workerRuntimeId":"integration-worker-runtime","shardId":"local-shard-1","sourceActorDefinitionId":"mob.feral_wolf","sourceDisplayName":"Feral Wolf","positionX":1.5,"positionY":0,"positionZ":-2,"rotationX":0,"rotationY":0,"rotationZ":0,"rotationW":1,"presentationKey":"corpse.remains","lifetimeSeconds":90.25,"workerHeartbeatTimeoutSeconds":30,"loot":[{"grantId":"{{lootGrantId}}","definitionId":"fixture.ore","quantity":3}]}"""),
            null,
            null);

        // Live Mob loot through a simulation worker actor.
        var session = await world.JoinAsync(looter);
        var looterState = await world.LoadStateAsync(looter.Character.Id);
        var inventoryRevision = await world.LoadContainerRevisionAsync(looterState.PermanentInventoryContainerId);
        var grantId = Guid.NewGuid();
        var sourceCorpseId = Guid.NewGuid();
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<GrantMobLootCommand>(
                grantId,
                SimulationActor(session),
                new GrantMobLootCommand(
                    looter.Character.Id,
                    looterState.Revision,
                    grantId,
                    sourceCorpseId,
                    "mob.feral_wolf",
                    "fixture.ore",
                    2,
                    looterState.PermanentInventoryContainerId,
                    inventoryRevision,
                    0)),
            CancellationToken.None));
        await AssertOperationAsync(
            context,
            grantId,
            ItemOperationKinds.GrantMobLoot,
            Canonical(
                $$"""{"authority":3,"accountId":"{{session.AccountId}}","requiresOfflineCharacter":false,"simulationSessionId":"{{session.SimulationSessionId}}","characterId":"{{session.CharacterId}}","workerId":"local-simulation-worker-1","workerRuntimeId":"integration-worker-runtime","shardId":"local-shard-1"}""",
                $$"""{"characterId":"{{looter.Character.Id}}","expectedCharacterRevision":{{looterState.Revision}},"grantId":"{{grantId}}","sourceCorpseId":"{{sourceCorpseId}}","sourceActorDefinitionId":"mob.feral_wolf","definitionId":"fixture.ore","quantity":2,"destinationContainerId":"{{looterState.PermanentInventoryContainerId}}","expectedDestinationContainerRevision":{{inventoryRevision}},"destinationSlotIndex":0}"""),
            looter.Registration.AccountId,
            looter.Character.Id);

        // Recovery expiry.
        var itemId = await world.GrantAsync(
            looter.Character.Id,
            "fixture.ore",
            1,
            looterState.PermanentInventoryContainerId,
            1);
        var deliveryId = await world.AddRecoveryDeliveryAsync(
            looter.Character.Id,
            itemId,
            DateTime.UtcNow.AddMinutes(5));
        await world.ExecuteSqlAsync(
            """
            update recovery_deliveries
            set created_at = now() - interval '2 seconds',
                expires_at = now() - interval '1 second'
            where id = @DeliveryId;
            """,
            new { DeliveryId = deliveryId });
        var expiryOperationId = Guid.NewGuid();
        AssertCommitted(await service.ExecuteAsync(
            new ItemTransactionRequest<ExpireRecoveryDeliveryCommand>(
                expiryOperationId,
                ItemTransactionActor.ForSystem(),
                new ExpireRecoveryDeliveryCommand(deliveryId)),
            CancellationToken.None));
        await AssertOperationAsync(
            context,
            expiryOperationId,
            ItemOperationKinds.ExpireRecoveryDelivery,
            Canonical(SystemActor, $$"""{"recoveryDeliveryId":"{{deliveryId}}"}"""),
            null,
            null);
    }

    private static ItemTransactionRequest<TCommand> Request<TCommand>(TCommand command)
        where TCommand : notnull
    {
        return new ItemTransactionRequest<TCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            command);
    }

    private static async Task AssertStoredAsync(
        PostgresIntegrationTestContext context,
        ItemTransactionResult result,
        string operationKind,
        string commandJson,
        Guid? expectedActorCharacterId = null)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(operationKind, result.OperationKind);
        await AssertOperationAsync(
            context,
            result.OperationId,
            operationKind,
            Canonical(SystemActor, commandJson),
            null,
            expectedActorCharacterId);
    }

    private static async Task AssertOperationAsync(
        PostgresIntegrationTestContext context,
        Guid operationId,
        string operationKind,
        string canonicalJson,
        Guid? actorAccountId,
        Guid? actorCharacterId)
    {
        var row = await LoadOperationAsync(context, operationId);
        Assert.Equal(operationKind, row.OperationKind);
        Assert.Equal(Hash(canonicalJson), row.RequestHash);
        Assert.Equal(actorAccountId, row.ActorAccountId);
        Assert.Equal(actorCharacterId, row.ActorCharacterId);
    }

    private static async Task<OperationRow> LoadOperationAsync(
        PostgresIntegrationTestContext context,
        Guid operationId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<OperationRow>(
            """
            select
                operation_kind as "OperationKind",
                request_hash as "RequestHash",
                actor_account_id as "ActorAccountId",
                actor_character_id as "ActorCharacterId"
            from item_operations
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId });
    }

    private static async Task<Guid> LoadCorpseIdAsync(
        PostgresIntegrationTestContext context,
        Guid deathEventId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<Guid>(
            "select corpse_id from death_events where id = @DeathEventId;",
            new { DeathEventId = deathEventId });
    }

    private static string Canonical(string actorJson, string commandJson)
    {
        return $$"""{"actor":{{actorJson}},"command":{{commandJson}}}""";
    }

    private static string Hash(string json)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private sealed class OperationRow
    {
        public string OperationKind { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public Guid? ActorAccountId { get; set; }

        public Guid? ActorCharacterId { get; set; }
    }
}
