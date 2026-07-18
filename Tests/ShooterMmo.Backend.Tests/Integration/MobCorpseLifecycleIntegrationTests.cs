using AuthService.Items;
using AuthService.Simulation;
using Dapper;
using SimulationWorker.Corpses;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class MobCorpseLifecycleIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task RetriedLiveMobLootGrantCreatesExactlyOnePersistentItem()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "mob-live-loot@example.com",
            "mob_live_loot",
            "Mob Live Looter");
        var session = await JoinAsync(context, player);
        var snapshot = await GetSnapshotAsync(context, player);
        var destinationSlot = snapshot.PermanentInventory.Slots.First(slot => slot.Item is null);
        var request = new SimulationMobLootGrantRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            snapshot.ItemStateRevision,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            Guid.NewGuid(),
            "mob.feral_wolf",
            "material.iron_ore",
            3,
            snapshot.PermanentInventory.ContainerId,
            snapshot.PermanentInventory.Revision,
            destinationSlot.SlotIndex);

        var first = await context.CorpseService.GrantLiveMobLootAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        var retry = await context.CorpseService.GrantLiveMobLootAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(retry.Succeeded, retry.Error?.Message);
        var itemId = Assert.Single(first.Value!.ItemRevisions).ItemInstanceId;
        Assert.Equal(itemId, Assert.Single(retry.Value!.ItemRevisions).ItemInstanceId);
        Assert.Equal(first.Value.OperationId, retry.Value.OperationId);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<int>(
                "select quantity from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
        Assert.Equal(
            snapshot.PermanentInventory.ContainerId,
            await connection.ExecuteScalarAsync<Guid>(
                "select container_id from item_instances where id = @ItemId;",
                new { ItemId = itemId }));
    }

    [PostgresIntegrationFact]
    public async Task DurableBossCorpseSurvivesWorkerRestartWithSameExpiryAndCustody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var operationId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var request = new CreatePersistentMobCorpseRequest(
            operationId,
            corpseId,
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            "mob.feral_alpha",
            "Feral Alpha",
            4d,
            0d,
            -3d,
            0d,
            0d,
            0d,
            1d,
            MobCorpseLifecycleService.DefaultPresentationKey,
            600d,
            [new MobCorpseLootEntryRequest(grantId, "material.iron_ore", 5)]);

        var first = await context.CorpseService.CreatePersistentMobCorpseAsync(
            WorkerId,
            request,
            CancellationToken.None);
        var retry = await context.CorpseService.CreatePersistentMobCorpseAsync(
            WorkerId,
            request,
            CancellationToken.None);
        var restored = await context.CorpseService.ListForWorkerAsync(
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(retry.Succeeded, retry.Error?.Message);
        Assert.True(restored.Succeeded, restored.Error?.Message);
        Assert.Equal(first.Value!.OperationId, retry.Value!.OperationId);
        Assert.Equal(first.Value.Corpse.CorpseId, retry.Value.Corpse.CorpseId);
        Assert.Equal(first.Value.Corpse.CreatedAt, retry.Value.Corpse.CreatedAt);
        Assert.Equal(first.Value.Corpse.ExpiresAt, retry.Value.Corpse.ExpiresAt);
        Assert.Null(first.Value.Corpse.SourceCharacterId);
        Assert.Equal(TimeSpan.FromMinutes(10),
            first.Value.Corpse.ExpiresAt - first.Value.Corpse.CreatedAt);
        var restoredCorpse = Assert.Single(
            restored.Value!.Corpses,
            corpse => corpse.CorpseId == corpseId);
        Assert.Equal(first.Value.Corpse.ExpiresAt, restoredCorpse.ExpiresAt);
        Assert.Equal(first.Value.Corpse.Sections, restoredCorpse.Sections);

        var restartedStore = new DurableCorpseStore(TimeProvider.System);
        restartedStore.Replace(
            new SimulationWorker.Auth.CorpseRestoreResponse(
                restored.Value.DatabaseTime,
                restored.Value.Corpses.Select(corpse =>
                    new SimulationWorker.Auth.DurableCorpseResponse(
                        corpse.CorpseId,
                        corpse.SourceCharacterId,
                        corpse.SourceDisplayName,
                        corpse.ShardId,
                        corpse.PositionX,
                        corpse.PositionY,
                        corpse.PositionZ,
                        corpse.RotationX,
                        corpse.RotationY,
                        corpse.RotationZ,
                        corpse.RotationW,
                        corpse.PresentationKey,
                        corpse.Revision,
                        corpse.CreatedAt,
                        corpse.ExpiresAt,
                        corpse.IsEmpty,
                        corpse.Sections.Select(section =>
                            new SimulationWorker.Auth.CorpseSectionResponse(
                                section.SectionKind,
                                section.ContainerId,
                                section.ContainerRevision,
                                section.ItemCount))
                            .ToArray()))
                    .ToArray()),
            ShardId);
        var inMemoryAfterRestart = Assert.Single(
            restartedStore.ListActive(),
            corpse => corpse.CorpseId == corpseId);
        Assert.Equal(first.Value.Corpse.ExpiresAt, inMemoryAfterRestart.ExpiresAt);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            corpseId,
            await connection.ExecuteScalarAsync<Guid>(
                """
                select section.corpse_id
                from item_instances item
                join corpse_sections section on section.container_id = item.container_id
                where item.definition_id = 'material.iron_ore';
                """));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from corpses where id = @CorpseId and closed_at is null;",
                new { CorpseId = corpseId }));

        await connection.ExecuteAsync(
            """
            update corpses
            set created_at = now() - interval '2 seconds',
                expires_at = now() - interval '1 second'
            where id = @CorpseId;
            """,
            new { CorpseId = corpseId });
        Assert.Equal(1, await context.CorpseService.ExpireDueCorpsesAsync(
            10,
            CancellationToken.None));
        Assert.Equal(
            "expired",
            await connection.ExecuteScalarAsync<string>(
                "select close_reason from corpses where id = @CorpseId;",
                new { CorpseId = corpseId }));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "select count(*) from item_destructions where reason = 'corpse_expired';"));
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
}
