using AuthService.Characters;
using AuthService.Http;
using AuthService.Items;
using AuthService.Simulation;
using Microsoft.AspNetCore.Http;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class AuthSimulationFlowIntegrationTests
{
    private const string LocalWorkerId = "local-simulation-worker-1";
    private const string LocalRuntimeId = "integration-worker-runtime";
    private const string LocalShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task RegistrationCharacterAndSimulationSessionLifecyclePersistsAcrossServices()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await context.InitializeDatabaseAsync();

        Assert.Equal(12, await CountFoundationTablesAsync(context));
        Assert.Equal(10, await CountAppliedMigrationsAsync(context));

        var player = await context.RegisterPlayerAsync();
        var characters = await context.CharacterService.ListAsync(
            player.Registration.AccountId,
            CancellationToken.None);

        Assert.Single(characters.Value!);
        Assert.Equal(player.Character.Id, characters.Value!.Single().Id);

        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);

        Assert.True(join.Succeeded, join.Error?.Message);
        Assert.False(join.Value!.IsReconnect);
        Assert.Equal(LocalShardId, join.Value.Shard.Id);
        Assert.Equal("local-world-1", join.Value.Shard.WorldId);
        Assert.Equal(LocalWorkerId, join.Value.Endpoint.WorkerId);
        Assert.Equal(LocalRuntimeId, join.Value.Endpoint.RuntimeId);

        var wrongWorker = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value.JoinTicket,
                "another-worker",
                LocalRuntimeId,
                LocalShardId),
            CancellationToken.None);

        Assert.False(wrongWorker.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, wrongWorker.StatusCode);
        Assert.Equal("wrong_simulation_worker", wrongWorker.Error!.Code);
        Assert.Equal(1, await CountActiveTicketsAsync(context));

        var consumed = await ConsumeLocalTicketAsync(context, join.Value.JoinTicket);

        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(player.Character.Id, consumed.Value!.CharacterId);
        Assert.Equal("Integration Hero", consumed.Value.CharacterName);
        Assert.Equal(LocalShardId, consumed.Value.ShardId);
        Assert.Equal("local-world-1", consumed.Value.WorldId);
        Assert.Equal(LocalWorkerId, consumed.Value.WorkerId);
        Assert.Equal(LocalRuntimeId, consumed.Value.WorkerRuntimeId);
        Assert.NotEqual(Guid.Empty, consumed.Value.SimulationSessionId);
        Assert.False(string.IsNullOrWhiteSpace(consumed.Value.SimulationSessionToken));
        Assert.Equal(0, consumed.Value.CarriedWeight);
        Assert.Equal(200, consumed.Value.CarryCapacity);
        Assert.True(consumed.Value.ItemStateRevision >= 0);
        Assert.False(consumed.Value.IsReconnect);
        Assert.Equal(1, await CountActiveSimulationSessionsAsync(context));

        var consumedAgain = await ConsumeLocalTicketAsync(context, join.Value.JoinTicket);
        Assert.False(consumedAgain.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, consumedAgain.StatusCode);

        var heartbeat = await context.SimulationSessionService.HeartbeatAsync(
            consumed.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(consumed.Value.SimulationSessionToken),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.True(heartbeat.Value.ExpiresAt >= consumed.Value.SessionExpiresAt);
        Assert.Equal(consumed.Value.ItemStateRevision, heartbeat.Value.ItemStateRevision);
        Assert.Equal(consumed.Value.CarriedWeight, heartbeat.Value.CarriedWeight);
        Assert.Equal(consumed.Value.CarryCapacity, heartbeat.Value.CarryCapacity);

        var firstRelease = await context.SimulationSessionService.ReleaseAsync(
            consumed.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(consumed.Value.SimulationSessionToken),
            CancellationToken.None);
        var secondRelease = await context.SimulationSessionService.ReleaseAsync(
            consumed.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(consumed.Value.SimulationSessionToken),
            CancellationToken.None);

        Assert.True(firstRelease.Succeeded, firstRelease.Error?.Message);
        Assert.True(secondRelease.Succeeded, secondRelease.Error?.Message);
        Assert.True(firstRelease.Value!.Released);
        Assert.True(secondRelease.Value!.Released);
        Assert.Equal(0, await CountActiveSimulationSessionsAsync(context));

        var heartbeatAfterRelease = await context.SimulationSessionService.HeartbeatAsync(
            consumed.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(consumed.Value.SimulationSessionToken),
            CancellationToken.None);

        Assert.False(heartbeatAfterRelease.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, heartbeatAfterRelease.StatusCode);
    }

    [PostgresIntegrationFact]
    public async Task CarryStateIsFencedIntoJoinHeartbeatAndReconnect()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "carry-join@example.com",
            "carry_join_player",
            "Carry Join Hero");
        var actor = ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId);
        var snapshot = (await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None)).Value!;
        var grantBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "bag.field_pack",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(grantBag.Succeeded, grantBag.Error?.Message);
        var bag = Assert.Single(grantBag.ItemRevisions);

        snapshot = (await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None)).Value!;
        var equipBag = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                actor,
                new EquipItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    bag.ItemInstanceId,
                    bag.Revision,
                    "bag")),
            CancellationToken.None);
        Assert.True(equipBag.Succeeded, equipBag.Error?.Message);

        snapshot = (await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None)).Value!;
        var grantAmmunition = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                actor,
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    "ammunition.training_556",
                    60,
                    snapshot.EquippedBag!.Contents.ContainerId,
                    6)),
            CancellationToken.None);
        Assert.True(grantAmmunition.Succeeded, grantAmmunition.Error?.Message);

        snapshot = (await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None)).Value!;
        Assert.Equal(60, snapshot.CarriedWeight);
        Assert.Equal(250, snapshot.CarryCapacity);

        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await ConsumeLocalTicketAsync(context, join.Value!.JoinTicket);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(snapshot.ItemStateRevision, consumed.Value!.ItemStateRevision);
        Assert.Equal(snapshot.CarriedWeight, consumed.Value.CarriedWeight);
        Assert.Equal(snapshot.CarryCapacity, consumed.Value.CarryCapacity);

        var grantWhileActive = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    consumed.Value.ItemStateRevision,
                    "ring.starter_band",
                    1,
                    snapshot.PermanentInventory.ContainerId,
                    0)),
            CancellationToken.None);
        Assert.True(grantWhileActive.Succeeded, grantWhileActive.Error?.Message);
        var committedCarry = Assert.Single(grantWhileActive.CharacterRevisions);
        Assert.True(committedCarry.Revision > consumed.Value.ItemStateRevision);
        Assert.Equal(61, committedCarry.CarriedWeight);
        Assert.Equal(250, committedCarry.CarryCapacity);

        var heartbeat = await context.SimulationSessionService.HeartbeatAsync(
            consumed.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(consumed.Value.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(committedCarry.Revision, heartbeat.Value!.ItemStateRevision);
        Assert.Equal(committedCarry.CarriedWeight, heartbeat.Value.CarriedWeight);
        Assert.Equal(committedCarry.CarryCapacity, heartbeat.Value.CarryCapacity);

        var reconnectTicket = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(reconnectTicket.Succeeded, reconnectTicket.Error?.Message);
        var reconnect = await ConsumeLocalTicketAsync(
            context,
            reconnectTicket.Value!.JoinTicket);
        Assert.True(reconnect.Succeeded, reconnect.Error?.Message);
        Assert.True(reconnect.Value!.IsReconnect);
        Assert.Equal(heartbeat.Value.ItemStateRevision, reconnect.Value.ItemStateRevision);
        Assert.Equal(heartbeat.Value.CarriedWeight, reconnect.Value.CarriedWeight);
        Assert.Equal(heartbeat.Value.CarryCapacity, reconnect.Value.CarryCapacity);
    }

    [PostgresIntegrationFact]
    public async Task PlacementUsesAuthoritativeSessionsWithoutWaitingForWorkerHeartbeat()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);
        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest() with
            {
                MaxConnections = 1,
                ActiveConnections = 0
            },
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);

        var firstPlayer = await context.RegisterPlayerAsync();
        var firstJoin = await context.ShardService.CreateJoinTicketAsync(
            firstPlayer.Account,
            LocalShardId,
            new JoinShardRequest(firstPlayer.Character.Id),
            CancellationToken.None);
        Assert.True(firstJoin.Succeeded, firstJoin.Error?.Message);

        var firstSession = await ConsumeLocalTicketAsync(context, firstJoin.Value!.JoinTicket);
        Assert.True(firstSession.Succeeded, firstSession.Error?.Message);

        var secondPlayer = await context.RegisterPlayerAsync(
            "capacity@example.com",
            "capacity_player",
            "Capacity Hero");
        var rejectedJoin = await context.ShardService.CreateJoinTicketAsync(
            secondPlayer.Account,
            LocalShardId,
            new JoinShardRequest(secondPlayer.Character.Id),
            CancellationToken.None);

        Assert.False(rejectedJoin.Succeeded);
        Assert.Equal("shard_unavailable", rejectedJoin.Error!.Code);

        var shards = await context.ShardService.ListShardsAsync(CancellationToken.None);
        var shard = Assert.Single(shards.Value!);
        Assert.False(shard.IsOnline);
        Assert.Equal(1, shard.ActivePlayers);
        Assert.Equal(1, shard.Capacity);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentTicketsReconnectAndCrossShardRulesKeepOneActiveSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "concurrency@example.com",
            "concurrency_player",
            "Concurrency Hero");

        var ticketTasks = Enumerable.Range(0, 2)
            .Select(_ => context.ShardService.CreateJoinTicketAsync(
                player.Account,
                LocalShardId,
                new JoinShardRequest(player.Character.Id),
                CancellationToken.None));

        var tickets = await Task.WhenAll(ticketTasks);
        Assert.All(tickets, result => Assert.True(result.Succeeded, result.Error?.Message));
        Assert.Equal(1, await CountActiveTicketsAsync(context));

        var consumedSessions = new List<ConsumedSimulationJoinTicketResponse>();
        foreach (var ticket in tickets)
        {
            var consume = await ConsumeLocalTicketAsync(context, ticket.Value!.JoinTicket);
            if (consume.Succeeded)
            {
                consumedSessions.Add(consume.Value!);
            }
            else
            {
                Assert.Equal(StatusCodes.Status401Unauthorized, consume.StatusCode);
            }
        }

        var firstSession = Assert.Single(consumedSessions);
        Assert.Equal(1, await CountActiveSimulationSessionsAsync(context));

        var secondCharacter = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Concurrency Alt"),
            CancellationToken.None);
        Assert.True(secondCharacter.Succeeded, secondCharacter.Error?.Message);

        var secondCharacterJoin = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(secondCharacter.Value!.Id),
            CancellationToken.None);
        Assert.False(secondCharacterJoin.Succeeded);
        Assert.Equal("account_character_already_active", secondCharacterJoin.Error!.Code);

        var reconnectTicket = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(reconnectTicket.Succeeded, reconnectTicket.Error?.Message);
        Assert.True(reconnectTicket.Value!.IsReconnect);

        var reconnect = await ConsumeLocalTicketAsync(
            context,
            reconnectTicket.Value.JoinTicket);
        Assert.True(reconnect.Succeeded, reconnect.Error?.Message);
        Assert.True(reconnect.Value!.IsReconnect);
        Assert.Equal(firstSession.SimulationSessionId, reconnect.Value.SimulationSessionId);
        Assert.NotEqual(
            firstSession.SimulationSessionToken,
            reconnect.Value.SimulationSessionToken);

        await context.AddShardAsync("local-shard-2");

        var crossShardWhileActive = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            "local-shard-2",
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.False(crossShardWhileActive.Succeeded);
        Assert.Equal("character_already_active", crossShardWhileActive.Error!.Code);

        var staleTokenRelease = await context.SimulationSessionService.ReleaseAsync(
            firstSession.SimulationSessionId,
            LocalWorkerId,
            Credential(firstSession.SimulationSessionToken),
            CancellationToken.None);
        Assert.False(staleTokenRelease.Succeeded);
        Assert.Equal(1, await CountActiveSimulationSessionsAsync(context));

        var release = await context.SimulationSessionService.ReleaseAsync(
            reconnect.Value.SimulationSessionId,
            LocalWorkerId,
            Credential(reconnect.Value.SimulationSessionToken),
            CancellationToken.None);
        Assert.True(release.Succeeded, release.Error?.Message);

        var crossShardAfterRelease = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            "local-shard-2",
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(crossShardAfterRelease.Succeeded, crossShardAfterRelease.Error?.Message);
        Assert.Equal("worker-local-shard-2", crossShardAfterRelease.Value!.Endpoint.WorkerId);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentMigrationInitializationAppliesEachMigrationOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            initializeDatabase: false);

        await Task.WhenAll(
            context.InitializeDatabaseAsync(),
            context.InitializeDatabaseAsync());
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);

        Assert.Equal(12, await CountFoundationTablesAsync(context));
        Assert.Equal(10, await CountAppliedMigrationsAsync(context));
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                "select count(*) from item_catalog_revisions where is_current;"));
        Assert.Equal(
            catalog.Definitions.Length,
            await context.ExecuteScalarIntAsync("select count(*) from item_definitions;"));
        Assert.Equal(
            catalog.EquipmentSlots.Length,
            await context.ExecuteScalarIntAsync("select count(*) from equipment_slots;"));
        Assert.Equal(
            5,
            await context.ExecuteScalarIntAsync(
                """
                select count(*)
                from pg_indexes
                where schemaname = 'public'
                  and indexname in (
                      'ux_account_sessions_active_account',
                      'ux_character_simulation_sessions_active_account',
                      'ux_character_simulation_sessions_active_character',
                      'ux_simulation_join_tickets_active_character',
                      'ux_simulation_assignments_active_shard');
                """));
    }

    [PostgresIntegrationFact]
    public async Task SecondLoginReplacesPreviousSessionAndSimulationAccess()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var firstJoin = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(firstJoin.Succeeded, firstJoin.Error?.Message);

        var firstSimulationSession = await ConsumeLocalTicketAsync(
            context,
            firstJoin.Value!.JoinTicket);
        Assert.True(firstSimulationSession.Succeeded, firstSimulationSession.Error?.Message);

        var secondCharacter = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new AuthService.Characters.CreateCharacterRequest("Second Hero"),
            CancellationToken.None);
        Assert.True(secondCharacter.Succeeded, secondCharacter.Error?.Message);
        var secondCharacterId = secondCharacter.Value!.Id;

        var pendingTicket = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(pendingTicket.Succeeded, pendingTicket.Error?.Message);
        Assert.True(pendingTicket.Value!.IsReconnect);

        var secondLogin = await context.AccountService.LoginAsync(
            new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
            CancellationToken.None);
        Assert.True(secondLogin.Succeeded, secondLogin.Error?.Message);

        Assert.Equal(0, await CountActiveTicketsAsync(context));
        Assert.Equal(0, await CountActiveSimulationSessionsAsync(context));

        var replacedHeartbeat = await context.SimulationSessionService.HeartbeatAsync(
            firstSimulationSession.Value!.SimulationSessionId,
            LocalWorkerId,
            Credential(firstSimulationSession.Value.SimulationSessionToken),
            CancellationToken.None);
        Assert.False(replacedHeartbeat.Succeeded);
        Assert.Equal(
            AuthService.Auth.AccountSessionErrorCode.SessionReplaced,
            replacedHeartbeat.Error!.Code);

        var replacementAccount = await context.SessionService.AuthenticateTokenAsync(
            secondLogin.Value!.SessionToken,
            CancellationToken.None);
        Assert.NotNull(replacementAccount);

        var replacementJoin = await context.ShardService.CreateJoinTicketAsync(
            replacementAccount!,
            LocalShardId,
            new JoinShardRequest(secondCharacterId),
            CancellationToken.None);
        Assert.True(replacementJoin.Succeeded, replacementJoin.Error?.Message);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentLoginsLeaveExactlyOneActiveAccountSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var logins = await Task.WhenAll(
            context.AccountService.LoginAsync(
                new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
                CancellationToken.None),
            context.AccountService.LoginAsync(
                new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
                CancellationToken.None));

        Assert.All(logins, login => Assert.True(login.Succeeded, login.Error?.Message));
        Assert.Null(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None));

        var authenticatedLoginCount = 0;
        foreach (var login in logins)
        {
            if (await context.SessionService.AuthenticateTokenAsync(
                    login.Value!.SessionToken,
                    CancellationToken.None) is not null)
            {
                authenticatedLoginCount++;
            }
        }

        Assert.Equal(1, authenticatedLoginCount);
        Assert.Equal(1, await CountActiveAccountSessionsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task SeedShardRequiresFreshWorkerHeartbeatAndRejectsStaleRuntime()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);

        var beforeHeartbeat = await context.ShardService.ListShardsAsync(CancellationToken.None);
        var offlineShard = Assert.Single(beforeHeartbeat.Value!);
        Assert.False(offlineShard.IsOnline);
        Assert.Equal(0, offlineShard.Capacity);

        var firstStartedAt = DateTime.UtcNow.AddMinutes(-2);
        var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(
                runtimeId: "first-runtime",
                host: "worker.test.local",
                udpPort: 28015) with
            {
                StartedAt = firstStartedAt
            },
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(LocalShardId, heartbeat.Value!.ShardId);
        Assert.Equal("local-world-1", heartbeat.Value.WorldId);
        Assert.Equal("worker.test.local", heartbeat.Value.Host);
        Assert.Equal(28015, heartbeat.Value.UdpPort);

        var afterHeartbeat = await context.ShardService.ListShardsAsync(CancellationToken.None);
        var onlineShard = Assert.Single(afterHeartbeat.Value!);
        Assert.True(onlineShard.IsOnline);
        Assert.Equal(100, onlineShard.Capacity);

        await context.ExecuteAsync(
            "update simulation_workers set last_heartbeat_at = now() - interval '1 hour';");
        var afterTimeout = await context.ShardService.ListShardsAsync(CancellationToken.None);
        Assert.False(Assert.Single(afterTimeout.Value!).IsOnline);

        var replacementHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest("replacement-runtime") with
            {
                StartedAt = firstStartedAt.AddMinutes(1)
            },
            CancellationToken.None);
        Assert.True(replacementHeartbeat.Succeeded, replacementHeartbeat.Error?.Message);

        var staleHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest("first-runtime") with
            {
                StartedAt = firstStartedAt
            },
            CancellationToken.None);
        Assert.False(staleHeartbeat.Succeeded);
        Assert.Equal("worker_runtime_changed", staleHeartbeat.Error!.Code);

        var staleOffline = await context.SimulationWorkerRegistryService.MarkOfflineAsync(
            LocalWorkerId,
            new SimulationWorkerOfflineRequest("first-runtime"),
            CancellationToken.None);
        Assert.False(staleOffline.Succeeded);
        Assert.Equal("worker_runtime_changed", staleOffline.Error!.Code);

        var offline = await context.SimulationWorkerRegistryService.MarkOfflineAsync(
            LocalWorkerId,
            new SimulationWorkerOfflineRequest("replacement-runtime"),
            CancellationToken.None);
        Assert.True(offline.Succeeded, offline.Error?.Message);

        var afterOffline = await context.ShardService.ListShardsAsync(CancellationToken.None);
        Assert.False(Assert.Single(afterOffline.Value!).IsOnline);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where released_at is null;"));
    }

    [PostgresIntegrationFact]
    public async Task ShardAssignmentFencesHealthyWorkerAndAllowsTimedOutWorkerFailover()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);

        var firstHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            "simulation-worker-a",
            PostgresIntegrationTestContext.CreateHeartbeatRequest("runtime-a"),
            CancellationToken.None);
        Assert.True(firstHeartbeat.Succeeded, firstHeartbeat.Error?.Message);

        var conflictingHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            "simulation-worker-b",
            PostgresIntegrationTestContext.CreateHeartbeatRequest("runtime-b"),
            CancellationToken.None);
        Assert.False(conflictingHeartbeat.Succeeded);
        Assert.Equal("shard_assignment_conflict", conflictingHeartbeat.Error!.Code);

        await context.ExecuteAsync(
            "update simulation_workers set last_heartbeat_at = now() - interval '1 hour' where id = 'simulation-worker-a';");

        var failoverHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            "simulation-worker-b",
            PostgresIntegrationTestContext.CreateHeartbeatRequest("runtime-b"),
            CancellationToken.None);
        Assert.True(failoverHeartbeat.Succeeded, failoverHeartbeat.Error?.Message);
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where worker_id = 'simulation-worker-b' and released_at is null;"));
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where worker_id = 'simulation-worker-a' and released_at is null;"));
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_workers where id = 'simulation-worker-a' and is_online;"));
    }

    [PostgresIntegrationFact]
    public async Task NewWorkerRuntimeReleasesOldAccessWhenWorkerMovesToAnotherShard()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);

        var firstStartedAt = DateTime.UtcNow.AddMinutes(-2);
        var firstHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest("first-runtime") with
            {
                StartedAt = firstStartedAt
            },
            CancellationToken.None);
        Assert.True(firstHeartbeat.Succeeded, firstHeartbeat.Error?.Message);

        var player = await context.RegisterPlayerAsync();
        var firstJoin = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(firstJoin.Succeeded, firstJoin.Error?.Message);

        var firstSession = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                firstJoin.Value!.JoinTicket,
                LocalWorkerId,
                "first-runtime",
                LocalShardId),
            CancellationToken.None);
        Assert.True(firstSession.Succeeded, firstSession.Error?.Message);

        var reconnectTicket = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(reconnectTicket.Succeeded, reconnectTicket.Error?.Message);
        Assert.True(reconnectTicket.Value!.IsReconnect);

        await context.ExecuteAsync(
            """
            insert into shards (id, display_name, world_id, fleet_id, rule_set)
            values ('secondary-shard', 'Secondary Shard', 'local-world-1', 'local-fleet', 'mvp-open-risk');
            """);

        var replacementHeartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
            LocalWorkerId,
            PostgresIntegrationTestContext.CreateHeartbeatRequest(
                runtimeId: "replacement-runtime",
                workerShardId: "secondary-shard") with
            {
                StartedAt = firstStartedAt.AddMinutes(1)
            },
            CancellationToken.None);
        Assert.True(replacementHeartbeat.Succeeded, replacementHeartbeat.Error?.Message);

        Assert.Equal(0, await CountActiveTicketsAsync(context));
        Assert.Equal(0, await CountActiveSimulationSessionsAsync(context));
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where shard_id = 'local-shard-1' and released_at is null;"));
        Assert.Equal(
            1,
            await context.ExecuteScalarIntAsync(
                "select count(*) from simulation_assignments where worker_id = 'local-simulation-worker-1' and shard_id = 'secondary-shard' and released_at is null;"));
    }

    private static Task<ServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeLocalTicketAsync(
        PostgresIntegrationTestContext context,
        string ticket)
    {
        return context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                ticket,
                LocalWorkerId,
                LocalRuntimeId,
                LocalShardId),
            CancellationToken.None);
    }

    private static SimulationSessionCredentialRequest Credential(string token)
    {
        return new SimulationSessionCredentialRequest(LocalRuntimeId, token);
    }

    private static Task<int> CountFoundationTablesAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            """
            select count(*)
            from information_schema.tables
            where table_schema = 'public'
              and table_name in (
                  'accounts',
                  'account_sessions',
                  'characters',
                  'world_definitions',
                  'fleets',
                  'simulation_nodes',
                  'shards',
                  'simulation_workers',
                  'simulation_assignments',
                  'simulation_join_tickets',
                  'character_simulation_sessions',
                  'schema_migrations');
            """);
    }

    private static Task<int> CountAppliedMigrationsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync("select count(*) from schema_migrations;");
    }

    private static Task<int> CountActiveTicketsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from simulation_join_tickets where consumed_at is null;");
    }

    private static Task<int> CountActiveAccountSessionsAsync(
        PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from account_sessions where revoked_at is null;");
    }

    private static Task<int> CountActiveSimulationSessionsAsync(
        PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from character_simulation_sessions where released_at is null;");
    }
}
