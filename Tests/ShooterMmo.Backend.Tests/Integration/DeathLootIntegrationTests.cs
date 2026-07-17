using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Auth;
using AuthService.Database.Migrations;
using AuthService.Items;
using AuthService.Simulation;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class DeathLootIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private const string CorpsePresentationKey = "corpse.generic_loot_crate";

    [PostgresIntegrationFact]
    public async Task CorpseMigrationIsIdempotentAndEnforcesPlayerLifetimeAndSnapshotShape()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();

        await context.InitializeDatabaseAsync();

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from schema_migrations where id = @MigrationId;",
                new { MigrationId = PlayerCorpsePersistenceMigration.Id }));
        Assert.Equal(
            4,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from information_schema.tables
                where table_schema = 'public'
                  and table_name in (
                      'corpses',
                      'corpse_sections',
                      'corpse_snapshots',
                      'death_events');
                """));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'corpse_snapshots'
                  and column_name = 'item_instance_id';
                """));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from pg_constraint
                where conname = 'ck_character_item_states_hard_cap';
                """));

        var player = await context.RegisterPlayerAsync(
            "corpse-schema@example.com",
            "corpse_schema_player",
            "Corpse Schema Hero");
        var death = await ProcessDeathAsync(context, player, Guid.NewGuid());
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            death.Corpse.ExpiresAt - death.Corpse.CreatedAt);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            update corpses
            set expires_at = expires_at + interval '1 second'
            where id = @CorpseId;
            """,
            new { death.Corpse.CorpseId }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_corpses_expiry", exception.ConstraintName);
    }

    [PostgresIntegrationFact]
    public async Task RepeatedDeathPartitionsEveryCustodyOnceAndPreservesCurrencyAndSecureContents()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "death-partition@example.com",
            "death_partition_player",
            "Death Partition Hero");
        var fixture = new DeathInventoryFixture(context, player);
        var initial = await fixture.GetSnapshotAsync();

        var inventoryOre = await fixture.GrantAsync(
            "material.iron_ore",
            3,
            initial.PermanentInventory.ContainerId,
            0);
        var protectedRing = await fixture.GrantAsync(
            "ring.starter_band",
            1,
            initial.PermanentInventory.ContainerId,
            1);
        await fixture.ApplyProtectedAsync(protectedRing, "death-test-protection");
        var insuredArmor = await fixture.GrantAsync(
            "armor.starter_vest",
            1,
            initial.PermanentInventory.ContainerId,
            2);
        await fixture.EquipAsync(insuredArmor, "body_armor");
        await fixture.ApplyInsuranceAsync(insuredArmor, "death-test-insurance");
        var bag = await fixture.GrantAsync(
            "bag.field_pack",
            1,
            initial.PermanentInventory.ContainerId,
            3);
        await fixture.EquipAsync(bag, "bag");
        var equippedBag = (await fixture.GetSnapshotAsync()).EquippedBag!;
        var bagChild = await fixture.GrantAsync(
            "ammunition.training_556",
            8,
            equippedBag.Contents.ContainerId,
            0);
        var secureItem = await fixture.GrantAsync(
            "medical.field_dressing",
            2,
            initial.SecureContainer.Contents.ContainerId,
            0);
        var bankItem = await fixture.GrantAsync(
            "material.iron_ore",
            2,
            initial.Bank.ContainerId,
            0);
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "update characters set currency = 12345 where id = @CharacterId;",
                new { CharacterId = player.Character.Id });
        }

        var beforeDeath = await fixture.GetSnapshotAsync();
        var deathEventId = Guid.NewGuid();
        var command = CreateDeathCommand(
            deathEventId,
            player.Character.Id,
            beforeDeath.ItemStateRevision,
            ShardId);
        var firstOperationId = Guid.NewGuid();
        var first = await context.CorpseService.ProcessSystemDeathAsync(
            firstOperationId,
            command,
            CancellationToken.None);
        Assert.True(first.Succeeded, first.Error?.Message);
        var partition = first.Value!;
        Assert.Equal(firstOperationId, partition.OperationId);
        Assert.Equal(deathEventId, partition.DeathEventId);
        Assert.False(partition.Corpse.IsEmpty);
        Assert.Equal(CorpsePresentationKey, partition.Corpse.PresentationKey);
        Assert.Equal(TimeSpan.FromMinutes(5), partition.Corpse.ExpiresAt - partition.Corpse.CreatedAt);
        Assert.Equal(2, partition.RecoveryDeliveryIds.Count);
        AssertSectionCount(partition.Corpse, "general_inventory", 1);
        AssertSectionCount(partition.Corpse, "equipment", 1);
        AssertSectionCount(partition.Corpse, "bag", 1);

        var allItemIds = new[]
        {
            inventoryOre,
            protectedRing,
            insuredArmor,
            bag,
            bagChild,
            secureItem,
            bankItem
        };
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                allItemIds.Length,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from item_instances where id = any(@ItemIds);",
                    new { ItemIds = allItemIds }));
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from item_destructions where item_instance_id = any(@ItemIds);",
                    new { ItemIds = allItemIds }));
            Assert.Equal(
                12345,
                await connection.ExecuteScalarAsync<long>(
                    "select currency from characters where id = @CharacterId;",
                    new { CharacterId = player.Character.Id }));

            var generalContainerId = GetSection(partition.Corpse, "general_inventory").ContainerId;
            var equipmentContainerId = GetSection(partition.Corpse, "equipment").ContainerId;
            var bagContainerId = GetSection(partition.Corpse, "bag").ContainerId;
            await AssertContainerAsync(connection, inventoryOre, generalContainerId);
            await AssertContainerAsync(connection, bag, equipmentContainerId);
            await AssertContainerAsync(connection, bagChild, bagContainerId);
            await AssertContainerAsync(
                connection,
                protectedRing,
                initial.RecoveryStorage.ContainerId);
            await AssertContainerAsync(
                connection,
                insuredArmor,
                initial.RecoveryStorage.ContainerId);
            await AssertContainerAsync(
                connection,
                secureItem,
                initial.SecureContainer.Contents.ContainerId);
            await AssertContainerAsync(connection, bankItem, initial.Bank.ContainerId);

            Assert.Equal(
                "consumed",
                await LoadPolicyStatusAsync(connection, insuredArmor, ItemPolicyIds.Insured));
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from corpse_snapshots
                    where corpse_id = @CorpseId
                      and snapshot_kind = 'insured_equipment'
                      and definition_id = 'armor.starter_vest'
                      and equipment_slot_id = 'body_armor';
                    """,
                    new { partition.Corpse.CorpseId }));
            var securePayload = await connection.ExecuteScalarAsync<string>(
                """
                select presentation_payload::text
                from corpse_snapshots
                where corpse_id = @CorpseId
                  and snapshot_kind = 'secure_container';
                """,
                new { partition.Corpse.CorpseId });
            Assert.DoesNotContain(
                secureItem.ToString("D"),
                securePayload,
                StringComparison.OrdinalIgnoreCase);
        }

        var replayOperationId = Guid.NewGuid();
        var replay = await context.CorpseService.ProcessSystemDeathAsync(
            replayOperationId,
            command,
            CancellationToken.None);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.Equal(replayOperationId, replay.Value!.OperationId);
        Assert.Equal(partition.Corpse.CorpseId, replay.Value.Corpse.CorpseId);
        Assert.Equal(partition.Corpse.ExpiresAt, replay.Value.Corpse.ExpiresAt);
        Assert.Equal(partition.RecoveryDeliveryIds, replay.Value.RecoveryDeliveryIds);
        Assert.Equal(partition.CharacterRevision.Revision, replay.Value.CharacterRevision.Revision);

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from death_events where id = @DeathEventId;",
                    new { DeathEventId = deathEventId }));
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from corpses where id = @CorpseId;",
                    new { partition.Corpse.CorpseId }));
            Assert.Equal(
                2,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from recovery_deliveries
                    where source_event_id = @SourceEventId;
                    """,
                    new { SourceEventId = deathEventId.ToString("D") }));
        }
    }

    [PostgresIntegrationFact]
    public async Task InsuredAndProtectedBagsRecoverEmptyWhileLootableChildrenStayOnCorpse()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var insuredPlayer = await context.RegisterPlayerAsync(
            "insured-bag@example.com",
            "insured_bag_player",
            "Insured Bag Hero");
        var insuredFixture = new DeathInventoryFixture(context, insuredPlayer);
        var insuredInitial = await insuredFixture.GetSnapshotAsync();
        var insuredBag = await insuredFixture.GrantAsync(
            "bag.field_pack",
            1,
            insuredInitial.PermanentInventory.ContainerId,
            0);
        await insuredFixture.EquipAsync(insuredBag, "bag");
        var insuredSourceContainer = (await insuredFixture.GetSnapshotAsync()).EquippedBag!.Contents;
        var insuredChild = await insuredFixture.GrantAsync(
            "material.iron_ore",
            2,
            insuredSourceContainer.ContainerId,
            0);
        var insuredBagProtectedChild = await insuredFixture.GrantAsync(
            "ring.starter_band",
            1,
            insuredSourceContainer.ContainerId,
            1);
        await insuredFixture.ApplyProtectedAsync(
            insuredBagProtectedChild,
            "protected-bag-child");
        var insuredBagInsuredChild = await insuredFixture.GrantAsync(
            "ring.starter_band",
            1,
            insuredSourceContainer.ContainerId,
            2);
        await insuredFixture.ApplyInsuranceAsync(
            insuredBagInsuredChild,
            "insured-bag-child");
        await insuredFixture.ApplyInsuranceAsync(insuredBag, "insured-bag-policy");

        var insuredDeath = await ProcessDeathAsync(context, insuredPlayer, Guid.NewGuid());
        var insuredCorpseBag = GetSection(insuredDeath.Corpse, "bag");
        Assert.Equal(1, insuredCorpseBag.ItemCount);

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await AssertRecoveryBagAsync(
                connection,
                insuredBag,
                insuredChild,
                insuredSourceContainer.ContainerId,
                insuredCorpseBag.ContainerId,
                "consumed");
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from corpse_snapshots
                    where corpse_id = @CorpseId
                      and snapshot_kind = 'insured_bag'
                      and jsonb_array_length(presentation_payload -> 'slots') = 7;
                    """,
                    new { insuredDeath.Corpse.CorpseId }));
            Assert.Equal(
                "insurance",
                await LoadRecoverySourceAsync(connection, insuredBag));
            await AssertContainerAsync(
                connection,
                insuredBagProtectedChild,
                insuredInitial.RecoveryStorage.ContainerId);
            await AssertContainerAsync(
                connection,
                insuredBagInsuredChild,
                insuredInitial.RecoveryStorage.ContainerId);
            Assert.Equal(
                "death_protection",
                await LoadRecoverySourceAsync(connection, insuredBagProtectedChild));
            Assert.Equal(
                "insurance",
                await LoadRecoverySourceAsync(connection, insuredBagInsuredChild));
            Assert.Equal(
                "active",
                await LoadPolicyStatusAsync(
                    connection,
                    insuredBagProtectedChild,
                    ItemPolicyIds.ProtectedOnDeath));
            Assert.Equal(
                "consumed",
                await LoadPolicyStatusAsync(
                    connection,
                    insuredBagInsuredChild,
                    ItemPolicyIds.Insured));
        }

        var protectedPlayer = await context.RegisterPlayerAsync(
            "protected-bag@example.com",
            "protected_bag_player",
            "Protected Bag Hero");
        var protectedFixture = new DeathInventoryFixture(context, protectedPlayer);
        var protectedInitial = await protectedFixture.GetSnapshotAsync();
        var protectedBag = await protectedFixture.GrantAsync(
            "bag.field_pack",
            1,
            protectedInitial.PermanentInventory.ContainerId,
            0);
        await protectedFixture.EquipAsync(protectedBag, "bag");
        var protectedSourceContainer = (await protectedFixture.GetSnapshotAsync()).EquippedBag!.Contents;
        var protectedChild = await protectedFixture.GrantAsync(
            "medical.field_dressing",
            2,
            protectedSourceContainer.ContainerId,
            0);
        await protectedFixture.ApplyInsuranceAsync(protectedBag, "protected-bag-insurance");
        await protectedFixture.ApplyProtectedAsync(protectedBag, "protected-bag-policy");

        var protectedDeath = await ProcessDeathAsync(context, protectedPlayer, Guid.NewGuid());
        var protectedCorpseBag = GetSection(protectedDeath.Corpse, "bag");
        Assert.Equal(1, protectedCorpseBag.ItemCount);

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            await AssertRecoveryBagAsync(
                connection,
                protectedBag,
                protectedChild,
                protectedSourceContainer.ContainerId,
                protectedCorpseBag.ContainerId,
                "active");
            Assert.Equal(
                "protected_bag",
                await LoadRecoverySourceAsync(connection, protectedBag));
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from corpse_snapshots
                    where corpse_id = @CorpseId
                      and snapshot_kind = 'protected_bag'
                      and policy_kind = 'protected_on_death';
                    """,
                    new { protectedDeath.Corpse.CorpseId }));
        }
    }

    [PostgresIntegrationFact]
    public async Task DeathAllowsSecureWeightOverflowAfterBagCapacityFallsButRejectsMoreWeight()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "death-overflow@example.com",
            "death_overflow_player",
            "Death Overflow Hero");
        var fixture = new DeathInventoryFixture(context, player);
        var initial = await fixture.GetSnapshotAsync();
        var bag = await fixture.GrantAsync(
            "bag.field_pack",
            1,
            initial.PermanentInventory.ContainerId,
            0);
        await fixture.EquipAsync(bag, "bag");
        var ore = await fixture.GrantAsync(
            "material.iron_ore",
            50,
            initial.SecureContainer.Contents.ContainerId,
            0);
        var beforeDeath = await fixture.GetSnapshotAsync();
        Assert.Equal(300, beforeDeath.CarriedWeight);
        Assert.Equal(250, beforeDeath.CarryCapacity);

        var death = await ProcessDeathAsync(context, player, Guid.NewGuid());
        Assert.Equal(300, death.CharacterRevision.CarriedWeight);
        Assert.Equal(200, death.CharacterRevision.CarryCapacity);
        AssertSectionCount(death.Corpse, "equipment", 1);

        var overflow = await fixture.GetSnapshotAsync();
        var reduceWeight = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<SplitItemStackCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new SplitItemStackCommand(
                    player.Character.Id,
                    overflow.ItemStateRevision,
                    ore,
                    await fixture.GetItemRevisionAsync(ore),
                    2,
                    initial.Bank.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(reduceWeight.Succeeded, reduceWeight.Error?.Message);
        var reduced = await fixture.GetSnapshotAsync();
        Assert.Equal(288, reduced.CarriedWeight);
        Assert.Equal(200, reduced.CarryCapacity);

        var rejectedOperationId = Guid.NewGuid();
        var addWeight = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                rejectedOperationId,
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    reduced.ItemStateRevision,
                    "medical.field_dressing",
                    1,
                    initial.PermanentInventory.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.False(addWeight.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CarryWeightLimitExceeded, addWeight.Error?.Code);
        var afterRejectedWeight = await fixture.GetSnapshotAsync();
        Assert.Equal(reduced.ItemStateRevision, afterRejectedWeight.ItemStateRevision);
        Assert.Equal(288, afterRejectedWeight.CarriedWeight);

        var clearWeight = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new RelocateItemCommand(
                    player.Character.Id,
                    afterRejectedWeight.ItemStateRevision,
                    ore,
                    await fixture.GetItemRevisionAsync(ore),
                    initial.Bank.ContainerId,
                    1)),
            CancellationToken.None);
        Assert.True(clearWeight.Succeeded, clearWeight.Error?.Message);
        var cleared = await fixture.GetSnapshotAsync();
        Assert.Equal(0, cleared.CarriedWeight);
        Assert.Equal(200, cleared.CarryCapacity);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_operation_changes where operation_id = @OperationId;",
                new { OperationId = rejectedOperationId }));
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                """
                select count(*)
                from item_instances
                where container_id = @ContainerId
                  and definition_id = 'medical.field_dressing';
                """,
                new { ContainerId = initial.PermanentInventory.ContainerId }));
    }

    [PostgresIntegrationFact]
    public async Task RestartRestoresOnlyOwnedUnexpiredShardCorpsesAndKeepsEmptyCorpseUntilExpiry()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var localPlayer = await context.RegisterPlayerAsync(
            "empty-corpse@example.com",
            "empty_corpse_player",
            "Empty Corpse Hero");
        var localDeath = await ProcessDeathAsync(context, localPlayer, Guid.NewGuid());
        Assert.True(localDeath.Corpse.IsEmpty);

        await context.AddShardAsync("secondary-shard");
        var secondaryPlayer = await context.RegisterPlayerAsync(
            "secondary-corpse@example.com",
            "secondary_corpse_player",
            "Secondary Corpse Hero");
        var secondaryDeath = await ProcessDeathAsync(
            context,
            secondaryPlayer,
            Guid.NewGuid(),
            "secondary-shard");

        var restartedService = new CorpseService(
            context.DataSource,
            new ItemTransactionService(context.DataSource),
            context.AuthServiceConfig,
            NullLogger<CorpseService>.Instance);
        var localRestore = await restartedService.ListForWorkerAsync(
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            CancellationToken.None);
        Assert.True(localRestore.Succeeded, localRestore.Error?.Message);
        var restoredLocal = Assert.Single(localRestore.Value!.Corpses);
        Assert.Equal(localDeath.Corpse.CorpseId, restoredLocal.CorpseId);
        Assert.Equal(localDeath.Corpse.ExpiresAt, restoredLocal.ExpiresAt);
        Assert.True(restoredLocal.IsEmpty);

        var secondaryRestore = await restartedService.ListForWorkerAsync(
            "worker-secondary-shard",
            "runtime-secondary-shard",
            "secondary-shard",
            CancellationToken.None);
        Assert.True(secondaryRestore.Succeeded, secondaryRestore.Error?.Message);
        Assert.Equal(
            secondaryDeath.Corpse.CorpseId,
            Assert.Single(secondaryRestore.Value!.Corpses).CorpseId);
        var wrongRuntime = await restartedService.ListForWorkerAsync(
            WorkerId,
            "stale-runtime",
            ShardId,
            CancellationToken.None);
        Assert.False(wrongRuntime.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.WorkerRuntimeChanged, wrongRuntime.Error!.Code);

        var earlyExpiry = await restartedService.ExpireCorpseAsync(
            localDeath.Corpse.CorpseId,
            CancellationToken.None);
        Assert.False(earlyExpiry.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.CorpseNotExpired, earlyExpiry.Error!.Code);

        await SetCorpseExpiredAsync(context, localDeath.Corpse.CorpseId);
        var afterDeadline = await restartedService.ListForWorkerAsync(
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            CancellationToken.None);
        Assert.True(afterDeadline.Succeeded, afterDeadline.Error?.Message);
        Assert.Empty(afterDeadline.Value!.Corpses);
        Assert.Equal(
            1,
            await restartedService.ExpireDueCorpsesAsync(64, CancellationToken.None));

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var lifecycle = await connection.QuerySingleAsync<CorpseLifecycleRow>(
            """
            select
                created_at as "CreatedAt",
                expires_at as "ExpiresAt",
                closed_at as "ClosedAt",
                close_reason as "CloseReason"
            from corpses
            where id = @CorpseId;
            """,
            new { localDeath.Corpse.CorpseId });
        Assert.Equal(TimeSpan.FromMinutes(5), lifecycle.ExpiresAt - lifecycle.CreatedAt);
        Assert.NotNull(lifecycle.ClosedAt);
        Assert.Equal("expired", lifecycle.CloseReason);
    }

    [PostgresIntegrationFact]
    public async Task ExpiryDestroysAllRemainingLootOnceWithDurableAudit()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "corpse-expiry@example.com",
            "corpse_expiry_player",
            "Corpse Expiry Hero");
        var fixture = new DeathInventoryFixture(context, player);
        var initial = await fixture.GetSnapshotAsync();
        var ore = await fixture.GrantAsync(
            "material.iron_ore",
            4,
            initial.PermanentInventory.ContainerId,
            0);
        var bag = await fixture.GrantAsync(
            "bag.field_pack",
            1,
            initial.PermanentInventory.ContainerId,
            1);
        await fixture.EquipAsync(bag, "bag");
        var bagContainer = (await fixture.GetSnapshotAsync()).EquippedBag!.Contents.ContainerId;
        var child = await fixture.GrantAsync(
            "medical.field_dressing",
            2,
            bagContainer,
            0);
        var death = await ProcessDeathAsync(context, player, Guid.NewGuid());
        var itemIds = new[] { ore, bag, child };
        await SetCorpseExpiredAsync(context, death.Corpse.CorpseId);

        var expired = await context.CorpseService.ExpireCorpseAsync(
            death.Corpse.CorpseId,
            CancellationToken.None);
        Assert.True(expired.Succeeded, expired.Error?.Message);
        var expiryOperationId = expired.Value!.OperationId;

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from item_instances where id = any(@ItemIds);",
                    new { ItemIds = itemIds }));
            Assert.Equal(
                itemIds.Length,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from item_destructions
                    where item_instance_id = any(@ItemIds)
                      and source_operation_id = @OperationId
                      and reason = 'corpse_expired';
                    """,
                    new { ItemIds = itemIds, OperationId = expiryOperationId }));
            Assert.Equal(
                itemIds.Length,
                await connection.ExecuteScalarAsync<int>(
                    """
                    select count(*)
                    from item_operation_changes
                    where operation_id = @OperationId
                      and change_kind = 'corpse_item_expired';
                    """,
                    new { OperationId = expiryOperationId }));
            Assert.Equal(
                "expired",
                await connection.ExecuteScalarAsync<string>(
                    "select close_reason from corpses where id = @CorpseId;",
                    new { death.Corpse.CorpseId }));
        }

        var replay = await context.CorpseService.ExpireCorpseAsync(
            death.Corpse.CorpseId,
            CancellationToken.None);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.Equal(expiryOperationId, replay.Value!.OperationId);

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                itemIds.Length,
                await connection.ExecuteScalarAsync<int>(
                    "select count(*) from item_destructions where item_instance_id = any(@ItemIds);",
                    new { ItemIds = itemIds }));
        }
    }

    [PostgresIntegrationFact]
    public async Task ClaimAndExpiryRaceLeavesExactlyOneFinalCustody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "corpse-race@example.com",
            "corpse_race_player",
            "Corpse Race Hero");
        var fixture = new DeathInventoryFixture(context, player);
        var initial = await fixture.GetSnapshotAsync();
        var itemId = await fixture.GrantAsync(
            "material.iron_ore",
            1,
            initial.PermanentInventory.ContainerId,
            0);
        var death = await ProcessDeathAsync(context, player, Guid.NewGuid());
        await SetCorpseExpiredAsync(context, death.Corpse.CorpseId);

        await using var claimConnection = await context.DataSource.OpenConnectionAsync();
        await using var claimTransaction = await claimConnection.BeginTransactionAsync();
        await claimConnection.ExecuteAsync(
            "select id from corpses where id = @CorpseId for update;",
            new { death.Corpse.CorpseId },
            claimTransaction);
        await claimConnection.ExecuteAsync(
            """
            select id
            from item_instances
            where id = @ItemId
            for update;

            update item_instances
            set container_id = @DestinationContainerId,
                container_slot_index = 0,
                revision = revision + 1,
                updated_at = now()
            where id = @ItemId;
            """,
            new
            {
                ItemId = itemId,
                DestinationContainerId = initial.PermanentInventory.ContainerId
            },
            claimTransaction);

        var expiryTask = context.CorpseService.ExpireCorpseAsync(
            death.Corpse.CorpseId,
            CancellationToken.None);
        await Task.Delay(100);
        await claimTransaction.CommitAsync();
        var expiry = await expiryTask;
        Assert.True(expiry.Succeeded, expiry.Error?.Message);

        await using var verificationConnection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            initial.PermanentInventory.ContainerId,
            await verificationConnection.ExecuteScalarAsync<Guid>(
                "select container_id from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            0,
            await verificationConnection.ExecuteScalarAsync<int>(
                "select count(*) from item_destructions where item_instance_id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            1,
            await verificationConnection.ExecuteScalarAsync<int>(
                """
                select
                    (select count(*) from item_instances where id = @ItemId)
                    +
                    (select count(*) from item_destructions where item_instance_id = @ItemId);
                """,
                new { ItemId = itemId }));
    }

    [PostgresIntegrationFact]
    public async Task CorpseEndpointsRequireServiceAuthenticationAndExactLiveAuthority()
    {
        const string workerSecret = "phase-ten-test-worker-secret-at-least-32-characters";
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "corpse-http@example.com",
            "corpse_http_player",
            "Corpse Http Hero");
        var snapshot = await GetSnapshotAsync(context, player);
        var session = await JoinAsync(context, player);
        var request = new SimulationPlayerDeathRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            snapshot.ItemStateRevision,
            10d,
            2d,
            -5d,
            0d,
            0d,
            0d,
            1d,
            CorpsePresentationKey);
        await using var host = await CorpseApiTestHost.StartAsync(
            context,
            WorkerId,
            workerSecret);
        var deathPath = $"/api/simulation-sessions/{session.SimulationSessionId}/player-deaths";

        using var unauthenticated = await host.Client.PostAsJsonAsync(deathPath, request);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("invalid_service_credentials", await ReadProblemCodeAsync(unauthenticated));

        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerIdHeader,
            WorkerId);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerSecretHeader,
            workerSecret);
        using var wrongWorker = await host.Client.PostAsJsonAsync(
            deathPath,
            request with
            {
                OperationId = Guid.NewGuid(),
                WorkerId = "another-worker"
            });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWorker.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WrongSimulationWorker,
            await ReadProblemCodeAsync(wrongWorker));

        using var wrongRuntime = await host.Client.PostAsJsonAsync(
            deathPath,
            request with
            {
                OperationId = Guid.NewGuid(),
                WorkerRuntimeId = "stale-runtime"
            });
        Assert.Equal(HttpStatusCode.Conflict, wrongRuntime.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WorkerRuntimeChanged,
            await ReadProblemCodeAsync(wrongRuntime));

        using var committed = await host.Client.PostAsJsonAsync(deathPath, request);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var partition = await committed.Content.ReadFromJsonAsync<PlayerDeathPartitionResponse>();
        Assert.NotNull(partition);
        Assert.Equal(request.DeathEventId, partition.DeathEventId);

        using var wrongOwner = await host.Client.GetAsync(
            $"/api/simulation-workers/another-worker/corpses?workerRuntimeId={WorkerRuntimeId}&shardId={ShardId}");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WrongSimulationWorker,
            await ReadProblemCodeAsync(wrongOwner));

        using var restored = await host.Client.GetAsync(
            $"/api/simulation-workers/{WorkerId}/corpses?workerRuntimeId={WorkerRuntimeId}&shardId={ShardId}");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var restore = await restored.Content.ReadFromJsonAsync<CorpseRestoreResponse>();
        Assert.NotNull(restore);
        Assert.Equal(partition.Corpse.CorpseId, Assert.Single(restore.Corpses).CorpseId);
    }

    [PostgresIntegrationFact]
    public async Task CorpseInteractionEndpointsEnforceAuthorityCommitAndCloseTransactions()
    {
        const string workerSecret = "phase-eleven-test-worker-secret-at-least-32-characters";
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "corpse-interaction-http@example.com",
            "corpse_interaction_http",
            "Corpse Interaction Hero");
        var fixture = new DeathInventoryFixture(context, player);
        var beforeDeath = await fixture.GetSnapshotAsync();
        var itemId = await fixture.GrantAsync(
            "medical.field_dressing",
            1,
            beforeDeath.PermanentInventory.ContainerId,
            0);
        var session = await JoinAsync(context, player);
        var partition = await ProcessDeathAsync(context, player, Guid.NewGuid());
        var afterDeath = await fixture.GetSnapshotAsync();
        var openRequest = new SimulationCorpseOpenRequest(
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken);
        var openPath = $"/api/simulation-sessions/{session.SimulationSessionId}"
            + $"/corpses/{partition.Corpse.CorpseId}/open";
        await using var host = await CorpseApiTestHost.StartAsync(
            context,
            WorkerId,
            workerSecret);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerIdHeader,
            WorkerId);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerSecretHeader,
            workerSecret);

        using var wrongWorker = await host.Client.PostAsJsonAsync(
            openPath,
            openRequest with { WorkerId = "another-worker" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongWorker.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WrongSimulationWorker,
            await ReadProblemCodeAsync(wrongWorker));

        using var wrongRuntime = await host.Client.PostAsJsonAsync(
            openPath,
            openRequest with { WorkerRuntimeId = "stale-runtime" });
        Assert.Equal(HttpStatusCode.Conflict, wrongRuntime.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.WorkerRuntimeChanged,
            await ReadProblemCodeAsync(wrongRuntime));

        using var openedResponse = await host.Client.PostAsJsonAsync(openPath, openRequest);
        Assert.Equal(HttpStatusCode.OK, openedResponse.StatusCode);
        var opened = await openedResponse.Content.ReadFromJsonAsync<CorpseViewSnapshotResponse>();
        Assert.NotNull(opened);
        var corpseItem = Assert.Single(
            opened.Sections.SelectMany(section => section.Slots),
            slot => slot.Item?.ItemInstanceId == itemId).Item!;
        var operationId = Guid.NewGuid();
        var mutationRequest = new SimulationCorpseMutationRequest(
            operationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            CorpseInteractionOperationKinds.LootItem,
            opened.Revision,
            corpseItem.ItemInstanceId,
            corpseItem.Revision,
            DestinationContainerId: afterDeath.PermanentInventory.ContainerId,
            ExpectedDestinationContainerRevision: afterDeath.PermanentInventory.Revision,
            DestinationSlotIndex: 0);
        var mutationPath = $"/api/simulation-sessions/{session.SimulationSessionId}"
            + $"/corpses/{partition.Corpse.CorpseId}/item-operations";

        using var mutatedResponse = await host.Client.PostAsJsonAsync(
            mutationPath,
            mutationRequest);
        Assert.Equal(HttpStatusCode.OK, mutatedResponse.StatusCode);
        var mutated = await mutatedResponse.Content.ReadFromJsonAsync<CorpseMutationResponse>();
        Assert.NotNull(mutated);
        Assert.True(mutated.Transaction.Succeeded);
        Assert.Equal(operationId, mutated.Transaction.OperationId);
        Assert.NotNull(mutated.Corpse);
        Assert.True(mutated.Corpse.Revision > opened.Revision);
        Assert.DoesNotContain(
            mutated.Corpse.Sections.SelectMany(section => section.Slots),
            slot => slot.Item?.ItemInstanceId == itemId);

        using var loserResponse = await host.Client.PostAsJsonAsync(
            mutationPath,
            mutationRequest with { OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, loserResponse.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.ItemAlreadyLooted,
            await ReadProblemCodeAsync(loserResponse));

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var idleTransactions = await connection.ExecuteScalarAsync<int>(
            """
            select count(*)
            from pg_stat_activity
            where datname = current_database()
              and pid <> pg_backend_pid()
              and state = 'idle in transaction';
            """);
        Assert.Equal(0, idleTransactions);
    }

    private static async Task<PlayerDeathPartitionResponse> ProcessDeathAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        Guid deathEventId,
        string shardId = ShardId)
    {
        var snapshot = await GetSnapshotAsync(context, player);
        var result = await context.CorpseService.ProcessSystemDeathAsync(
            Guid.NewGuid(),
            CreateDeathCommand(
                deathEventId,
                player.Character.Id,
                snapshot.ItemStateRevision,
                shardId),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static ProcessPlayerDeathCommand CreateDeathCommand(
        Guid deathEventId,
        Guid characterId,
        long characterRevision,
        string shardId)
    {
        return new ProcessPlayerDeathCommand(
            deathEventId,
            characterId,
            characterRevision,
            shardId,
            10d,
            2d,
            -5d,
            0d,
            0d,
            0d,
            1d,
            CorpsePresentationKey);
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
                WorkerRuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static CorpseSectionResponse GetSection(
        DurableCorpseResponse corpse,
        string sectionKind)
    {
        return Assert.Single(corpse.Sections, section => section.SectionKind == sectionKind);
    }

    private static void AssertSectionCount(
        DurableCorpseResponse corpse,
        string sectionKind,
        int expectedItemCount)
    {
        Assert.Equal(expectedItemCount, GetSection(corpse, sectionKind).ItemCount);
    }

    private static async Task AssertContainerAsync(
        NpgsqlConnection connection,
        Guid itemId,
        Guid expectedContainerId)
    {
        Assert.Equal(
            expectedContainerId,
            await connection.ExecuteScalarAsync<Guid>(
                "select container_id from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
    }

    private static Task<string?> LoadPolicyStatusAsync(
        NpgsqlConnection connection,
        Guid itemId,
        string policyKind)
    {
        return connection.ExecuteScalarAsync<string>(
            """
            select status
            from item_instance_policies
            where item_instance_id = @ItemId
              and policy_kind = @PolicyKind;
            """,
            new { ItemId = itemId, PolicyKind = policyKind });
    }

    private static Task<string?> LoadRecoverySourceAsync(
        NpgsqlConnection connection,
        Guid itemId)
    {
        return connection.ExecuteScalarAsync<string>(
            """
            select delivery.source_kind
            from recovery_delivery_items delivery_item
            join recovery_deliveries delivery
              on delivery.id = delivery_item.recovery_delivery_id
            where delivery_item.item_instance_id = @ItemId;
            """,
            new { ItemId = itemId });
    }

    private static async Task AssertRecoveryBagAsync(
        NpgsqlConnection connection,
        Guid bagItemId,
        Guid childItemId,
        Guid originalBagContainerId,
        Guid corpseBagContainerId,
        string expectedInsuranceStatus)
    {
        Assert.Equal(
            "recovery_storage",
            await connection.ExecuteScalarAsync<string>(
                """
                select container.container_type
                from item_instances item
                join item_containers container on container.id = item.container_id
                where item.id = @ItemId;
                """,
                new { ItemId = bagItemId }));
        await AssertContainerAsync(connection, childItemId, corpseBagContainerId);
        var originalContainer = await connection.QuerySingleAsync<BagContainerLifecycleRow>(
            """
            select
                lifecycle as "Lifecycle",
                (
                    select count(*)
                    from item_instances
                    where container_id = container.id) as "ItemCount"
            from item_containers container
            where id = @ContainerId;
            """,
            new { ContainerId = originalBagContainerId });
        Assert.Equal("closed", originalContainer.Lifecycle);
        Assert.Equal(0, originalContainer.ItemCount);
        Assert.Equal(
            expectedInsuranceStatus,
            await LoadPolicyStatusAsync(connection, bagItemId, ItemPolicyIds.Insured));
    }

    private static async Task SetCorpseExpiredAsync(
        PostgresIntegrationTestContext context,
        Guid corpseId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '6 minutes',
                expires_at = now() - interval '1 minute'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private sealed class DeathInventoryFixture(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        public Task<CharacterInventorySnapshotResponse> GetSnapshotAsync()
        {
            return DeathLootIntegrationTests.GetSnapshotAsync(context, player);
        }

        public async Task<Guid> GrantAsync(
            string definitionId,
            int quantity,
            Guid destinationContainerId,
            int destinationSlotIndex)
        {
            var snapshot = await GetSnapshotAsync();
            var result = await context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        player.Character.Id,
                        snapshot.ItemStateRevision,
                        definitionId,
                        quantity,
                        destinationContainerId,
                        destinationSlotIndex)),
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            return Assert.Single(result.ItemRevisions).ItemInstanceId;
        }

        public async Task EquipAsync(Guid itemId, string equipmentSlotId)
        {
            var snapshot = await GetSnapshotAsync();
            var itemRevision = await GetItemRevisionAsync(itemId);
            var result = await context.ItemTransactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new EquipItemCommand(
                        player.Character.Id,
                        snapshot.ItemStateRevision,
                        itemId,
                        itemRevision,
                        equipmentSlotId)),
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        public async Task ApplyInsuranceAsync(Guid itemId, string sourceId)
        {
            var snapshot = await GetSnapshotAsync();
            var result = await context.ItemPolicyService.ApplyInsuranceAsync(
                Guid.NewGuid(),
                player.Character.Id,
                snapshot.ItemStateRevision,
                itemId,
                await GetItemRevisionAsync(itemId),
                sourceId,
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        public async Task ApplyProtectedAsync(Guid itemId, string sourceId)
        {
            var snapshot = await GetSnapshotAsync();
            var result = await context.ItemPolicyService.ApplyProtectedOnDeathAsync(
                Guid.NewGuid(),
                player.Character.Id,
                snapshot.ItemStateRevision,
                itemId,
                await GetItemRevisionAsync(itemId),
                ItemPolicySourceKinds.CatalogDefault,
                sourceId,
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        public async Task<long> GetItemRevisionAsync(Guid itemId)
        {
            await using var connection = await context.DataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<long>(
                "select revision from item_instances where id = @ItemId;",
                new { ItemId = itemId });
        }
    }

    private sealed record BagContainerLifecycleRow(string Lifecycle, long ItemCount);

    private sealed record CorpseLifecycleRow(
        DateTime CreatedAt,
        DateTime ExpiresAt,
        DateTime? ClosedAt,
        string? CloseReason);
}
