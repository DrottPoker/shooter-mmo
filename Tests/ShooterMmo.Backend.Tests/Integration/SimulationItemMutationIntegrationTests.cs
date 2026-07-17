using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Auth;
using AuthService.Items;
using AuthService.Simulation;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class SimulationItemMutationIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task SimulationItemEndpointRequiresServiceAuthenticationAndExactWorkerIdentity()
    {
        const string workerSecret = "phase-eight-test-worker-secret-at-least-32-characters";
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "worker-http@example.com",
            "worker_http_player",
            "Worker Http Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "ring.starter_band",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        var item = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);
        var request = CreateRelocateRequest(
            session,
            snapshot.ItemStateRevision,
            item.ItemInstanceId,
            item.Revision,
            snapshot.PermanentInventory.ContainerId,
            1);
        await using var host = await SimulationItemApiTestHost.StartAsync(
            context,
            WorkerId,
            workerSecret);
        var path = $"/api/simulation-sessions/{session.SimulationSessionId}/item-operations";

        using var unauthenticated = await host.Client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("invalid_service_credentials", await ReadProblemCodeAsync(unauthenticated));

        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerIdHeader,
            WorkerId);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerSecretHeader,
            workerSecret);
        using var wrongWorker = await host.Client.PostAsJsonAsync(
            path,
            request with { WorkerId = "another-worker", OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWorker.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WrongSimulationWorker,
            await ReadProblemCodeAsync(wrongWorker));

        using var committed = await host.Client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var result = await committed.Content.ReadFromJsonAsync<ItemTransactionResult>();
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
    }

    [PostgresIntegrationFact]
    public async Task LiveMutationRejectsEveryMismatchedAuthorityBindingAndStaleAssignment()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "live-fence@example.com",
            "live_fence_player",
            "Live Fence Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "ring.starter_band",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        var item = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);

        var valid = CreateRelocateRequest(
            session,
            snapshot.ItemStateRevision,
            item.ItemInstanceId,
            item.Revision,
            snapshot.PermanentInventory.ContainerId,
            1);

        await AssertRejectedAsync(
            context,
            valid with { WorkerId = "another-worker", OperationId = Guid.NewGuid() },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.WrongSimulationWorker);
        await AssertRejectedAsync(
            context,
            valid with { WorkerRuntimeId = "another-runtime", OperationId = Guid.NewGuid() },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.WorkerRuntimeChanged);
        await AssertRejectedAsync(
            context,
            valid with { ShardId = "another-shard", OperationId = Guid.NewGuid() },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.SimulationSessionInvalid);
        await AssertRejectedAsync(
            context,
            valid with { OperationId = Guid.NewGuid() },
            Guid.NewGuid(),
            WorkerId,
            ItemTransactionErrorCodes.SimulationSessionInvalid);
        await AssertRejectedAsync(
            context,
            valid with
            {
                SessionToken = "wrong-simulation-session-token",
                OperationId = Guid.NewGuid()
            },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.SimulationSessionInvalid);
        await AssertRejectedAsync(
            context,
            valid with { AccountId = Guid.NewGuid(), OperationId = Guid.NewGuid() },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.ItemNotOwned);
        await AssertRejectedAsync(
            context,
            valid with { CharacterId = Guid.NewGuid(), OperationId = Guid.NewGuid() },
            session.SimulationSessionId,
            WorkerId,
            ItemTransactionErrorCodes.ItemNotOwned);

        var committed = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            valid,
            CancellationToken.None);
        Assert.True(committed.Succeeded, committed.Error?.Message);

        await context.ExecuteAsync(
            $"""
            update simulation_assignments
            set released_at = now()
            where worker_id = '{WorkerId}'
              and shard_id = '{ShardId}'
              and released_at is null;
            """);

        var stale = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            valid with
            {
                OperationId = Guid.NewGuid(),
                ExpectedCharacterRevision = committed.Value!.CharacterRevisions.Single().Revision,
                ExpectedItemRevision = committed.Value.ItemRevisions.Single().Revision,
                DestinationSlotIndex = 2
            },
            CancellationToken.None);
        Assert.False(stale.Succeeded);
        Assert.Equal(
            ItemTransactionErrorCodes.WrongSimulationWorker,
            stale.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task SecureMutationIsIdempotentUpdatesCarryAndRestoresOnReconnect()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "secure-live@example.com",
            "secure_live_player",
            "Secure Live Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "material.iron_ore",
            10,
            snapshot.SecureContainer.Contents.ContainerId,
            0);
        var item = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        Assert.Equal(60, snapshot.CarriedWeight);
        var session = await JoinAsync(context, player);
        var request = new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(false, false, false),
            ItemOperationKinds.Destroy,
            snapshot.ItemStateRevision,
            item.ItemInstanceId,
            item.Revision);

        var first = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        var duplicate = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(duplicate.Succeeded, duplicate.Error?.Message);
        Assert.Equal(first.Value!.OperationId, duplicate.Value!.OperationId);
        Assert.Equal(first.Value.OperationKind, duplicate.Value.OperationKind);
        Assert.Equal(
            first.Value.CharacterRevisions.ToArray(),
            duplicate.Value.CharacterRevisions.ToArray());
        Assert.Equal(
            first.Value.ContainerRevisions.ToArray(),
            duplicate.Value.ContainerRevisions.ToArray());
        Assert.Equal(
            first.Value.ItemRevisions.ToArray(),
            duplicate.Value.ItemRevisions.ToArray());
        Assert.Equal(
            first.Value.RecoveryDeliveryIds.ToArray(),
            duplicate.Value.RecoveryDeliveryIds.ToArray());
        var committedCarry = Assert.Single(first.Value!.CharacterRevisions);
        Assert.True(committedCarry.Revision > snapshot.ItemStateRevision);
        Assert.Equal(0, committedCarry.CarriedWeight);
        Assert.Equal(200, committedCarry.CarryCapacity);
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from item_operations where operation_id = '{request.OperationId}';"));

        var heartbeat = await context.SimulationSessionService.HeartbeatAsync(
            session.SimulationSessionId,
            WorkerId,
            Credential(session.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(committedCarry.Revision, heartbeat.Value!.ItemStateRevision);
        Assert.Equal(committedCarry.CarriedWeight, heartbeat.Value.CarriedWeight);
        Assert.Equal(committedCarry.CarryCapacity, heartbeat.Value.CarryCapacity);

        var reconnectTicket = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(reconnectTicket.Succeeded, reconnectTicket.Error?.Message);
        var reconnect = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                reconnectTicket.Value!.JoinTicket,
                WorkerId,
                RuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(reconnect.Succeeded, reconnect.Error?.Message);
        Assert.True(reconnect.Value!.IsReconnect);
        Assert.Equal(committedCarry.Revision, reconnect.Value.ItemStateRevision);
        Assert.Equal(committedCarry.CarriedWeight, reconnect.Value.CarriedWeight);
        Assert.Equal(committedCarry.CarryCapacity, reconnect.Value.CarryCapacity);
    }

    [PostgresIntegrationFact]
    public async Task BankAndRecoveryMutationsRequireWorkerValidatedCityAccess()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "city-access@example.com",
            "city_access_player",
            "City Access Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var bankGrant = await GrantAsync(
            context,
            player,
            snapshot,
            "ring.starter_band",
            1,
            snapshot.Bank.ContainerId,
            0);
        var bankItem = Assert.Single(bankGrant.ItemRevisions);

        snapshot = await GetSnapshotAsync(context, player);
        var recoveryGrant = await GrantAsync(
            context,
            player,
            snapshot,
            "medical.field_dressing",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        var recoveryItem = Assert.Single(recoveryGrant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var delivery = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new AddRecoveryDeliveryCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "phase8_test",
                    Guid.NewGuid().ToString("N"),
                    null,
                    null,
                    [new ItemRevisionExpectation(
                        recoveryItem.ItemInstanceId,
                        recoveryItem.Revision)])),
            CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Error?.Message);
        var deliveryId = Assert.Single(delivery.RecoveryDeliveryIds);
        snapshot = await GetSnapshotAsync(context, player);
        var recovery = Assert.Single(snapshot.RecoveryStorage.Deliveries);
        var deliveredItem = Assert.Single(recovery.Items).Item;
        var session = await JoinAsync(context, player);
        var noAccess = new SimulationItemAccessRequest(false, false, false);

        var bankResult = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            CreateRelocateRequest(
                session,
                snapshot.ItemStateRevision,
                bankItem.ItemInstanceId,
                bankItem.Revision,
                snapshot.PermanentInventory.ContainerId,
                1) with
            { Access = noAccess },
            CancellationToken.None);
        Assert.False(bankResult.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.BankAccessRequired, bankResult.Error!.Code);

        var recoveryResult = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                noAccess,
                ItemOperationKinds.ClaimRecoveryDelivery,
                snapshot.ItemStateRevision,
                DestinationContainerId: snapshot.PermanentInventory.ContainerId,
                RecoveryDeliveryId: deliveryId,
                ExpectedRecoveryDeliveryRevision: recovery.Items.Count > 0 ? 0 : -1,
                Items: [new ItemRevisionExpectation(
                    deliveredItem.ItemInstanceId,
                    deliveredItem.Revision)]),
            CancellationToken.None);
        Assert.False(recoveryResult.Succeeded);
        Assert.Equal(
            ItemTransactionErrorCodes.RecoveryAccessRequired,
            recoveryResult.Error!.Code);

        var after = await GetSnapshotAsync(context, player);
        Assert.Equal(snapshot.ItemStateRevision, after.ItemStateRevision);
        Assert.NotNull(after.Bank.Slots.Single(slot => slot.SlotIndex == 0).Item);
        Assert.Single(after.RecoveryStorage.Deliveries);
    }

    [PostgresIntegrationFact]
    public async Task AccountAndWorkerMutationRaceCannotCommitTwoLocations()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "authority-race@example.com",
            "authority_race_player",
            "Authority Race Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "ring.starter_band",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        var item = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);
        var workerRequest = CreateRelocateRequest(
            session,
            snapshot.ItemStateRevision,
            item.ItemInstanceId,
            item.Revision,
            snapshot.Bank.ContainerId,
            0) with
        {
            Access = new SimulationItemAccessRequest(true, false, false)
        };
        var accountRequest = new RelocateAccountItemRequest(
            Guid.NewGuid(),
            snapshot.ItemStateRevision,
            item.ItemInstanceId,
            item.Revision,
            snapshot.SecureContainer.Contents.ContainerId,
            0);

        var workerTask = context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            workerRequest,
            CancellationToken.None);
        var accountTask = context.AccountItemMutationService.RelocateAsync(
            player.Registration.AccountId,
            player.Character.Id,
            accountRequest,
            CancellationToken.None);
        await Task.WhenAll(workerTask, accountTask);

        Assert.True(workerTask.Result.Succeeded, workerTask.Result.Error?.Message);
        Assert.False(accountTask.Result.Succeeded);
        Assert.Contains(
            accountTask.Result.Error!.Code,
            new[]
            {
                ItemTransactionErrorCodes.OfflineAccessRequired,
                ItemTransactionErrorCodes.ItemStateConflict
            });

        var after = await GetSnapshotAsync(context, player);
        Assert.Equal(
            item.ItemInstanceId,
            after.Bank.Slots.Single(slot => slot.SlotIndex == 0).Item!.ItemInstanceId);
        Assert.Null(after.SecureContainer.Contents.Slots.Single(slot => slot.SlotIndex == 0).Item);
        Assert.Null(after.PermanentInventory.Slots.Single(slot => slot.SlotIndex == 0).Item);
    }

    [PostgresIntegrationFact]
    public async Task EquippedItemWeightIsExcludedAndUnequipRestoresCarriedWeight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "equipped-weight@example.com",
            "equipped_weight_player",
            "Equipped Weight Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await GrantAsync(
            context,
            player,
            snapshot,
            "weapon.training_rifle",
            1,
            snapshot.PermanentInventory.ContainerId,
            0);
        var rifle = Assert.Single(grant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        Assert.Equal(25, snapshot.CarriedWeight);
        var session = await JoinAsync(context, player);

        var equip = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                ItemOperationKinds.Equip,
                snapshot.ItemStateRevision,
                rifle.ItemInstanceId,
                rifle.Revision,
                EquipmentSlotId: "primary_weapon"),
            CancellationToken.None);

        Assert.True(equip.Succeeded, equip.Error?.Message);
        var equippedCarry = Assert.Single(equip.Value!.CharacterRevisions);
        var equippedRifle = Assert.Single(equip.Value.ItemRevisions);
        Assert.Equal(0, equippedCarry.CarriedWeight);
        Assert.Equal(200, equippedCarry.CarryCapacity);

        var unequip = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                ItemOperationKinds.Unequip,
                equippedCarry.Revision,
                equippedRifle.ItemInstanceId,
                equippedRifle.Revision,
                DestinationContainerId: snapshot.PermanentInventory.ContainerId,
                DestinationSlotIndex: 1),
            CancellationToken.None);

        Assert.True(unequip.Succeeded, unequip.Error?.Message);
        var unequippedCarry = Assert.Single(unequip.Value!.CharacterRevisions);
        Assert.Equal(25, unequippedCarry.CarriedWeight);
        Assert.Equal(200, unequippedCarry.CarryCapacity);
        var restored = await GetSnapshotAsync(context, player);
        Assert.Equal(25, restored.CarriedWeight);
        Assert.Equal(
            rifle.ItemInstanceId,
            restored.PermanentInventory.Slots.Single(slot => slot.SlotIndex == 1).Item!.ItemInstanceId);
    }

    [PostgresIntegrationFact]
    public async Task LiveContainerSwapIsAtomicWeightAwareAndRejectsIncompatibleOppositeSlot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "container-swap@example.com",
            "container_swap_player",
            "Container Swap Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        await GrantAsync(
            context,
            player,
            snapshot,
            "material.iron_ore",
            2,
            snapshot.PermanentInventory.ContainerId,
            0);
        snapshot = await GetSnapshotAsync(context, player);
        await GrantAsync(
            context,
            player,
            snapshot,
            "weapon.training_rifle",
            1,
            snapshot.Bank.ContainerId,
            0);
        snapshot = await GetSnapshotAsync(context, player);
        await GrantAsync(
            context,
            player,
            snapshot,
            "medical.field_dressing",
            1,
            snapshot.SecureContainer.Contents.ContainerId,
            0);
        snapshot = await GetSnapshotAsync(context, player);
        var bagGrant = await GrantAsync(
            context,
            player,
            snapshot,
            "bag.field_pack",
            1,
            snapshot.PermanentInventory.ContainerId,
            1);
        var bag = Assert.Single(bagGrant.ItemRevisions);
        snapshot = await GetSnapshotAsync(context, player);
        var equipBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId),
                new EquipItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    bag.ItemInstanceId,
                    bag.Revision,
                    "bag")),
            CancellationToken.None);
        Assert.True(equipBag.Succeeded, equipBag.Error?.Message);
        snapshot = await GetSnapshotAsync(context, player);
        await GrantAsync(
            context,
            player,
            snapshot,
            "medical.field_dressing",
            1,
            snapshot.EquippedBag!.Contents.ContainerId,
            4);
        snapshot = await GetSnapshotAsync(context, player);
        Assert.Equal(16, snapshot.CarriedWeight);
        Assert.Equal(250, snapshot.CarryCapacity);
        var ore = snapshot.PermanentInventory.Slots.Single(slot => slot.SlotIndex == 0).Item!;
        var rifle = snapshot.Bank.Slots.Single(slot => slot.SlotIndex == 0).Item!;
        var session = await JoinAsync(context, player);

        var swapped = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(true, false, false),
                ItemOperationKinds.SwapContainerItems,
                snapshot.ItemStateRevision,
                ore.ItemInstanceId,
                ore.Revision,
                TargetItemInstanceId: rifle.ItemInstanceId,
                ExpectedTargetItemRevision: rifle.Revision),
            CancellationToken.None);

        Assert.True(swapped.Succeeded, swapped.Error?.Message);
        Assert.Equal(ItemOperationKinds.SwapContainerItems, swapped.Value!.OperationKind);
        Assert.Equal(29, Assert.Single(swapped.Value.CharacterRevisions).CarriedWeight);
        Assert.Equal(2, swapped.Value.ItemRevisions.Count);

        var afterSwap = await GetSnapshotAsync(context, player);
        var permanentRifle = afterSwap.PermanentInventory.Slots
            .Single(slot => slot.SlotIndex == 0).Item!;
        var bankOre = afterSwap.Bank.Slots.Single(slot => slot.SlotIndex == 0).Item!;
        var secureMedical = afterSwap.SecureContainer.Contents.Slots
            .Single(slot => slot.SlotIndex == 0).Item!;
        var bagMedical = afterSwap.EquippedBag!.Contents.Slots
            .Single(slot => slot.SlotIndex == 4).Item!;
        Assert.Equal(rifle.ItemInstanceId, permanentRifle.ItemInstanceId);
        Assert.Equal(ore.ItemInstanceId, bankOre.ItemInstanceId);

        var specializedRejected = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                ItemOperationKinds.SwapContainerItems,
                afterSwap.ItemStateRevision,
                permanentRifle.ItemInstanceId,
                permanentRifle.Revision,
                TargetItemInstanceId: bagMedical.ItemInstanceId,
                ExpectedTargetItemRevision: bagMedical.Revision),
            CancellationToken.None);

        Assert.False(specializedRejected.Succeeded);
        Assert.Equal(
            ItemTransactionErrorCodes.ItemSlotIncompatible,
            specializedRejected.Error!.Code);

        var secureRejected = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationItemOperationRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                session.WorkerId,
                session.WorkerRuntimeId,
                session.ShardId,
                session.SimulationSessionToken,
                new SimulationItemAccessRequest(false, false, false),
                ItemOperationKinds.SwapContainerItems,
                afterSwap.ItemStateRevision,
                permanentRifle.ItemInstanceId,
                permanentRifle.Revision,
                TargetItemInstanceId: secureMedical.ItemInstanceId,
                ExpectedTargetItemRevision: secureMedical.Revision),
            CancellationToken.None);

        Assert.False(secureRejected.Succeeded);
        Assert.Equal(
            ItemTransactionErrorCodes.SecureContainerItemForbidden,
            secureRejected.Error!.Code);
        var afterRejection = await GetSnapshotAsync(context, player);
        Assert.Equal(afterSwap.ItemStateRevision, afterRejection.ItemStateRevision);
        Assert.Equal(
            permanentRifle.ItemInstanceId,
            afterRejection.PermanentInventory.Slots.Single(slot => slot.SlotIndex == 0).Item!.ItemInstanceId);
        Assert.Equal(
            bagMedical.ItemInstanceId,
            afterRejection.EquippedBag!.Contents.Slots.Single(slot => slot.SlotIndex == 4).Item!.ItemInstanceId);
        Assert.Equal(
            secureMedical.ItemInstanceId,
            afterRejection.SecureContainer.Contents.Slots.Single(slot => slot.SlotIndex == 0).Item!.ItemInstanceId);
    }

    private static async Task AssertRejectedAsync(
        PostgresIntegrationTestContext context,
        SimulationItemOperationRequest request,
        Guid simulationSessionId,
        string authenticatedWorkerId,
        string expectedCode)
    {
        var result = await context.SimulationItemMutationService.ExecuteAsync(
            authenticatedWorkerId,
            simulationSessionId,
            request,
            CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error!.Code);
    }

    private static SimulationItemOperationRequest CreateRelocateRequest(
        ConsumedSimulationJoinTicketResponse session,
        long characterRevision,
        Guid itemId,
        long itemRevision,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(false, false, false),
            ItemOperationKinds.Relocate,
            characterRevision,
            itemId,
            itemRevision,
            DestinationContainerId: destinationContainerId,
            DestinationSlotIndex: destinationSlotIndex);
    }

    private static async Task<ItemTransactionResult> GrantAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        CharacterInventorySnapshotResponse snapshot,
        string definitionId,
        int quantity,
        Guid destinationContainerId,
        int destinationSlotIndex)
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

    private static SimulationSessionCredentialRequest Credential(string token)
    {
        return new SimulationSessionCredentialRequest(RuntimeId, token);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}
