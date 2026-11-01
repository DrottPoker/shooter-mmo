using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class GameServiceClientTests
{
    [Fact]
    public async Task ConsumeJoinTicketSendsExactWorkerRuntimeAndShardBinding()
    {
        var simulationSessionId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "development-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId,
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            sessionLeaseDurationMilliseconds = 30_000,
            itemStateRevision = 12,
            carriedWeight = 210,
            carryCapacity = 200,
            isReconnect = false,
            appearance = Look()
        }));
        var client = CreateClient(handler);

        var result = await ConsumeAsync(client);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(simulationSessionId, result.Value!.SimulationSessionId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/simulation-join-tickets/consume", request.Path);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("join-ticket", body.RootElement.GetProperty("ticket").GetString());
        Assert.Equal(
            "local-simulation-worker-1",
            body.RootElement.GetProperty("workerId").GetString());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
        Assert.Equal("local-shard-1", body.RootElement.GetProperty("shardId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", request.CorrelationId);
    }

    [Fact]
    public async Task SimulationSessionLifecycleUsesRuntimeBoundRoutes()
    {
        var simulationSessionId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(request => CreateJsonResponse(new
        {
            simulationSessionId,
            characterId,
            shardId = "local-shard-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            expiresAt = DateTime.UtcNow.AddMinutes(1),
            leaseDurationMilliseconds = 30_000,
            itemStateRevision = 12,
            carriedWeight = 210,
            carryCapacity = 200,
            released = request.RequestUri!.AbsolutePath.EndsWith(
                "/release",
                StringComparison.Ordinal)
        }));
        var client = CreateClient(handler);

        var heartbeat = await client.HeartbeatSimulationSessionAsync(
            simulationSessionId,
            "runtime-1",
            "simulation-session-token",
            CancellationToken.None);
        var release = await client.ReleaseSimulationSessionAsync(
            simulationSessionId,
            "runtime-1",
            "simulation-session-token",
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.True(release.Succeeded, release.Error?.Message);
        Assert.True(release.Value!.Released);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(
                $"/api/simulation-sessions/{simulationSessionId}/heartbeat",
                request.Path),
            request => Assert.Equal(
                $"/api/simulation-sessions/{simulationSessionId}/release",
                request.Path));
    }

    [Fact]
    public async Task SimulationItemMutationSendsExactLiveAuthorityAndValidatesCommittedCarry()
    {
        var simulationSessionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var requestBody = new SimulationItemOperationRequest(
            operationId,
            accountId,
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            new SimulationItemAccessRequest(true, false, false),
            "relocate",
            12,
            itemId,
            3,
            DestinationContainerId: containerId,
            DestinationSlotIndex: 2);
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            operationId,
            operationKind = "relocate",
            succeeded = true,
            error = (object?)null,
            characterRevisions = new[]
            {
                new
                {
                    characterId,
                    revision = 13,
                    carriedWeight = 25,
                    carryCapacity = 200
                }
            },
            containerRevisions = new[] { new { containerId, revision = 4 } },
            itemRevisions = new[] { new { itemInstanceId = itemId, revision = 4 } },
            recoveryDeliveryIds = Array.Empty<Guid>(),
            characterState = CharacterStateTestData.Create(characterId, 13, 25)
        }));
        var client = CreateClient(handler);

        var result = await client.MutateSimulationItemsAsync(
            simulationSessionId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(13, result.Value!.CharacterRevisions[0].Revision);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"/api/simulation-sessions/{simulationSessionId}/item-operations",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(accountId, body.RootElement.GetProperty("accountId").GetGuid());
        Assert.Equal(characterId, body.RootElement.GetProperty("characterId").GetGuid());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("workerRuntimeId").GetString());
        Assert.True(body.RootElement.GetProperty("access").GetProperty("bank").GetBoolean());
    }

    [Fact]
    public async Task PlayerDeathSendsRuntimeBoundEventAndValidatesDurablePartition()
    {
        var simulationSessionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var deathEventId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        var requestBody = new SimulationPlayerDeathRequest(
            operationId,
            deathEventId,
            accountId,
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            12,
            1d,
            2d,
            3d,
            0d,
            0d,
            0d,
            1d,
            "corpse.remains",
            new SimulationDeathAmmunitionRequest(Guid.NewGuid(), 3, 2),
            new SimulationDeathKillerRequest("mob", "mob.wolf"));
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            operationId,
            deathEventId,
            corpse = new
            {
                corpseId = Guid.NewGuid(),
                sourceCharacterId = characterId,
                sourceDisplayName = "Fallen Hero",
                shardId = "local-shard-1",
                positionX = 1d,
                positionY = 2d,
                positionZ = 3d,
                rotationX = 0d,
                rotationY = 0d,
                rotationZ = 0d,
                rotationW = 1d,
                presentationKey = "corpse.remains",
                revision = 1,
                createdAt,
                expiresAt = createdAt.AddMinutes(5),
                isEmpty = true,
                sections = CreateCorpseSections()
            },
            characterRevision = new
            {
                characterId,
                revision = 13,
                carriedWeight = 0,
                carryCapacity = 200
            },
            recoveryDeliveryIds = Array.Empty<Guid>(),
            characterState = CharacterStateTestData.Create(characterId, 13)
        }));
        var client = CreateClient(handler);

        var result = await client.ProcessPlayerDeathAsync(
            simulationSessionId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(deathEventId, result.Value!.DeathEventId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"/api/simulation-sessions/{simulationSessionId}/player-deaths",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(deathEventId, body.RootElement.GetProperty("deathEventId").GetGuid());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("workerRuntimeId").GetString());
        Assert.Equal(
            "simulation-session-token",
            body.RootElement.GetProperty("sessionToken").GetString());
        var ammunition = body.RootElement.GetProperty("ammunitionConsumption");
        Assert.Equal(
            requestBody.AmmunitionConsumption!.ItemInstanceId,
            ammunition.GetProperty("itemInstanceId").GetGuid());
        Assert.Equal(3, ammunition.GetProperty("expectedItemRevision").GetInt64());
        Assert.Equal(2, ammunition.GetProperty("quantity").GetInt32());
        var killer = body.RootElement.GetProperty("killer");
        Assert.Equal("mob", killer.GetProperty("kind").GetString());
        Assert.Equal("mob.wolf", killer.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AmmunitionConsumptionSendsTheBatchAndValidatesTheCommittedState()
    {
        var simulationSessionId = Guid.NewGuid();
        var request = CreateAmmunitionRequest();
        var equipped = new SimulationEquippedItem(
            "ammunition",
            request.ItemInstanceId,
            "ammunition.bronze_arrow",
            45,
            4);
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(
            CreateAmmunitionResponse(
                request,
                CharacterStateTestData.Create(request.CharacterId, 13, equipment: [equipped]))));

        var result = await CreateClient(handler).ConsumeAmmunitionAsync(
            simulationSessionId,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(equipped, Assert.Single(result.Value!.CharacterState!.Equipment));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(
            $"/api/simulation-sessions/{simulationSessionId}/ammunition-consumptions",
            sent.Path);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(request.OperationId, body.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(12, body.RootElement.GetProperty("expectedCharacterRevision").GetInt64());
        Assert.Equal(
            request.ItemInstanceId,
            body.RootElement.GetProperty("itemInstanceId").GetGuid());
        Assert.Equal(3, body.RootElement.GetProperty("expectedItemRevision").GetInt64());
        Assert.Equal(5, body.RootElement.GetProperty("quantity").GetInt32());
    }

    [Theory]
    [InlineData("missing_state")]
    [InlineData("older_state")]
    [InlineData("other_character")]
    [InlineData("duplicate_slot")]
    [InlineData("invalid_definition")]
    [InlineData("unchanged_item")]
    [InlineData("other_operation_kind")]
    public async Task AmmunitionConsumptionRejectsAnInconsistentCommittedState(string variant)
    {
        var request = CreateAmmunitionRequest();
        var equipped = new SimulationEquippedItem(
            "ammunition",
            request.ItemInstanceId,
            "ammunition.bronze_arrow",
            45,
            4);
        var state = CharacterStateTestData.Create(request.CharacterId, 13, equipment: [equipped]);
        var valid = CreateAmmunitionResponse(request, state);
        var response = variant switch
        {
            "missing_state" => valid with { CharacterState = null },
            "older_state" => valid with { CharacterState = state with { ItemStateRevision = 12 } },
            "other_character" => valid with
            {
                CharacterState = state with { CharacterId = Guid.NewGuid() }
            },
            "duplicate_slot" => valid with
            {
                CharacterState = state with
                {
                    Equipment = [equipped, equipped with { ItemInstanceId = Guid.NewGuid() }]
                }
            },
            "invalid_definition" => valid with
            {
                CharacterState = state with
                {
                    Equipment = [equipped with { DefinitionId = "Bronze Arrow" }]
                }
            },
            "unchanged_item" => valid with
            {
                ItemRevisions = [new SimulationItemInstanceRevision(request.ItemInstanceId, 3)]
            },
            "other_operation_kind" => valid with { OperationKind = "consume_quantity" },
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
        };
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(response));

        var result = await CreateClient(handler).ConsumeAmmunitionAsync(
            Guid.NewGuid(),
            request,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task SimulationWorkerHeartbeatSendsCompleteTopologyIdentity()
    {
        var requestBody = CreateHeartbeatRequest();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            workerId = "local-simulation-worker-1",
            runtimeId = requestBody.RuntimeId,
            fleetId = requestBody.FleetId,
            nodeId = requestBody.NodeId,
            shardId = requestBody.ShardId,
            worldId = "development-world-1",
            ruleSet = "mvp-open-risk",
            host = requestBody.Host,
            udpPort = requestBody.UdpPort,
            maxConnections = requestBody.MaxConnections,
            activeConnections = requestBody.ActiveConnections,
            protocolVersion = requestBody.ProtocolVersion,
            simulationRevision = requestBody.SimulationRevision,
            collisionRevision = requestBody.CollisionRevision,
            itemCatalogRevision = requestBody.ItemCatalogRevision,
            lastHeartbeatAt = DateTime.UtcNow,
            onlineUntil = DateTime.UtcNow.AddSeconds(30)
        }));
        var client = CreateClient(handler);

        var result = await client.HeartbeatSimulationWorkerAsync(
            "local-simulation-worker-1",
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/heartbeat",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("local-fleet", body.RootElement.GetProperty("fleetId").GetString());
        Assert.Equal("local-node-1", body.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal("local-shard-1", body.RootElement.GetProperty("shardId").GetString());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
        Assert.Equal(
            RealtimeProtocol.Version,
            body.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(
            "item-catalog-revision-1",
            body.RootElement.GetProperty("itemCatalogRevision").GetString());
    }

    [Fact]
    public async Task SimulationWorkerOfflineUsesRuntimeGuardedRoute()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            workerId = "local-simulation-worker-1",
            runtimeId = "runtime-1",
            offlineAt = DateTime.UtcNow
        }));
        var client = CreateClient(handler);

        var result = await client.MarkSimulationWorkerOfflineAsync(
            "local-simulation-worker-1",
            "runtime-1",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/offline",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("runtime-1", body.RootElement.GetProperty("runtimeId").GetString());
    }

    [Fact]
    public async Task DurableCorpseRestoreUsesExactRuntimeShardAndValidatesSnapshot()
    {
        var databaseTime = DateTime.UtcNow;
        var corpseId = Guid.NewGuid();
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            databaseTime,
            corpses = new[]
            {
                new
                {
                    corpseId,
                    sourceCharacterId = Guid.NewGuid(),
                    sourceDisplayName = "Fallen Hero",
                    shardId = "local-shard-1",
                    positionX = 1d,
                    positionY = 2d,
                    positionZ = 3d,
                    rotationX = 0d,
                    rotationY = 0d,
                    rotationZ = 0d,
                    rotationW = 1d,
                    presentationKey = "corpse.remains",
                    revision = 1,
                    createdAt = databaseTime.AddMinutes(-1),
                    expiresAt = databaseTime.AddMinutes(4),
                    isEmpty = true,
                    sections = CreateCorpseSections()
                }
            }
        }));
        var client = CreateClient(handler);

        var result = await client.RestoreDurableCorpsesAsync(
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(corpseId, Assert.Single(result.Value!.Corpses).CorpseId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/simulation-workers/local-simulation-worker-1/corpses",
            request.Path);
        Assert.Contains("workerRuntimeId=runtime-1", request.Query, StringComparison.Ordinal);
        Assert.Contains("shardId=local-shard-1", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableCorpseRestoreRejectsExpiredOrCrossShardPayload()
    {
        var databaseTime = DateTime.UtcNow;
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            databaseTime,
            corpses = new[]
            {
                new
                {
                    corpseId = Guid.NewGuid(),
                    sourceCharacterId = Guid.NewGuid(),
                    sourceDisplayName = "Fallen Hero",
                    shardId = "another-shard",
                    positionX = 0d,
                    positionY = 0d,
                    positionZ = 0d,
                    rotationX = 0d,
                    rotationY = 0d,
                    rotationZ = 0d,
                    rotationW = 1d,
                    presentationKey = "corpse.remains",
                    revision = 0,
                    createdAt = databaseTime.AddMinutes(-5),
                    expiresAt = databaseTime,
                    isEmpty = true,
                    sections = CreateCorpseSections()
                }
            }
        }));

        var result = await CreateClient(handler).RestoreDurableCorpsesAsync(
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadGateway, (HttpStatusCode)result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task CorpseOpenSendsExactSessionAuthorityAndValidatesFullView()
    {
        var simulationSessionId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var requestBody = new SimulationCorpseOpenRequest(
            accountId,
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token");
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(
            CreateCorpseViewPayload(corpseId, characterId, "local-shard-1", 7)));

        var result = await CreateClient(handler).OpenCorpseAsync(
            simulationSessionId,
            corpseId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(7, result.Value!.Revision);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/open",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(accountId, body.RootElement.GetProperty("accountId").GetGuid());
        Assert.Equal(characterId, body.RootElement.GetProperty("characterId").GetGuid());
        Assert.Equal("runtime-1", body.RootElement.GetProperty("workerRuntimeId").GetString());
        Assert.Equal(
            "simulation-session-token",
            body.RootElement.GetProperty("sessionToken").GetString());
    }

    [Fact]
    public async Task CorpseMutationValidatesCommittedCarryAndAuthoritativeSnapshot()
    {
        var simulationSessionId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var destinationContainerId = Guid.NewGuid();
        var requestBody = new SimulationCorpseMutationRequest(
            operationId,
            accountId,
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            "loot_item",
            7,
            itemId,
            2,
            DestinationContainerId: destinationContainerId,
            ExpectedDestinationContainerRevision: 3,
            DestinationSlotIndex: 1);
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            transaction = new
            {
                operationId,
                operationKind = "loot_corpse_item",
                succeeded = true,
                error = (object?)null,
                characterRevisions = new[]
                {
                    new
                    {
                        characterId,
                        revision = 13,
                        carriedWeight = 30,
                        carryCapacity = 200
                    }
                },
                containerRevisions = new[]
                {
                    new { containerId = destinationContainerId, revision = 4 }
                },
                itemRevisions = new[] { new { itemInstanceId = itemId, revision = 3 } },
                recoveryDeliveryIds = Array.Empty<Guid>()
            },
            characterState = CharacterStateTestData.Create(characterId, 13, 30),
            corpse = CreateCorpseViewPayload(
                corpseId,
                characterId,
                "local-shard-1",
                8)
        }));

        var result = await CreateClient(handler).MutateCorpseAsync(
            simulationSessionId,
            corpseId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(13, result.Value!.Transaction.CharacterRevisions[0].Revision);
        Assert.Equal(8, result.Value.Corpse!.Revision);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"/api/simulation-sessions/{simulationSessionId}/corpses/{corpseId}/item-operations",
            request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("loot_item", body.RootElement.GetProperty("operationKind").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("expectedCorpseRevision").GetInt64());
    }

    [Theory]
    [InlineData("deposit_item", "deposit_corpse_item")]
    [InlineData("deposit_partial_stack", "deposit_corpse_partial_stack")]
    public async Task CorpseDepositMutationAcceptsCommittedAuthorityResponse(
        string requestOperationKind,
        string transactionOperationKind)
    {
        var simulationSessionId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var destinationContainerId = Guid.NewGuid();
        var requestBody = new SimulationCorpseMutationRequest(
            operationId,
            Guid.NewGuid(),
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            requestOperationKind,
            7,
            itemId,
            2,
            Quantity: requestOperationKind == "deposit_partial_stack" ? 1 : null,
            DestinationContainerId: destinationContainerId,
            ExpectedDestinationContainerRevision: 3,
            DestinationSlotIndex: 1);
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            transaction = new
            {
                operationId,
                operationKind = transactionOperationKind,
                succeeded = true,
                error = (object?)null,
                characterRevisions = new[]
                {
                    new
                    {
                        characterId,
                        revision = 13,
                        carriedWeight = 20,
                        carryCapacity = 200
                    }
                },
                containerRevisions = new[]
                {
                    new { containerId = destinationContainerId, revision = 4 }
                },
                itemRevisions = new[] { new { itemInstanceId = itemId, revision = 3 } },
                recoveryDeliveryIds = Array.Empty<Guid>()
            },
            characterState = CharacterStateTestData.Create(characterId, 13, 20),
            corpse = CreateCorpseViewPayload(
                corpseId,
                characterId,
                "local-shard-1",
                8)
        }));

        var result = await CreateClient(handler).MutateCorpseAsync(
            simulationSessionId,
            corpseId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(transactionOperationKind, result.Value!.Transaction.OperationKind);
        Assert.Equal(8, result.Value.Corpse!.Revision);
    }

    [Theory]
    [InlineData("move_item", "move_corpse_item")]
    [InlineData("move_partial_stack", "move_corpse_partial_stack")]
    public async Task CorpseInternalMoveAcceptsAuthorityResponseWithoutCharacterRevision(
        string requestOperationKind,
        string transactionOperationKind)
    {
        var simulationSessionId = Guid.NewGuid();
        var corpseId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var destinationContainerId = Guid.NewGuid();
        var requestBody = new SimulationCorpseMutationRequest(
            operationId,
            Guid.NewGuid(),
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            requestOperationKind,
            7,
            itemId,
            2,
            Quantity: requestOperationKind == "move_partial_stack" ? 1 : null,
            DestinationContainerId: destinationContainerId,
            ExpectedDestinationContainerRevision: 3,
            DestinationSlotIndex: 1);
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            transaction = new
            {
                operationId,
                operationKind = transactionOperationKind,
                succeeded = true,
                error = (object?)null,
                characterRevisions = Array.Empty<object>(),
                containerRevisions = new[]
                {
                    new { containerId = destinationContainerId, revision = 4 }
                },
                itemRevisions = new[] { new { itemInstanceId = itemId, revision = 3 } },
                recoveryDeliveryIds = Array.Empty<Guid>()
            },
            characterState = CharacterStateTestData.Create(characterId, 7),
            corpse = CreateCorpseViewPayload(
                corpseId,
                characterId,
                "local-shard-1",
                8)
        }));

        var result = await CreateClient(handler).MutateCorpseAsync(
            simulationSessionId,
            corpseId,
            requestBody,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(transactionOperationKind, result.Value!.Transaction.OperationKind);
        Assert.Empty(result.Value.Transaction.CharacterRevisions);
        Assert.Equal(8, result.Value.Corpse!.Revision);
    }

    [Fact]
    public async Task CorpseClientRejectsCrossShardViewAndInvalidCarryMutation()
    {
        var corpseId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var openRequest = new SimulationCorpseOpenRequest(
            Guid.NewGuid(),
            characterId,
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "token");
        var crossShardHandler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(
            CreateCorpseViewPayload(corpseId, characterId, "another-shard", 1)));

        var open = await CreateClient(crossShardHandler).OpenCorpseAsync(
            Guid.NewGuid(),
            corpseId,
            openRequest,
            CancellationToken.None);

        Assert.False(open.Succeeded);
        Assert.Equal("invalid_auth_response", open.Error!.Code);

        var operationId = Guid.NewGuid();
        var mutationRequest = new SimulationCorpseMutationRequest(
            operationId,
            openRequest.AccountId,
            characterId,
            openRequest.WorkerId,
            openRequest.WorkerRuntimeId,
            openRequest.ShardId,
            openRequest.SessionToken,
            "loot_item",
            1,
            Guid.NewGuid(),
            1,
            DestinationContainerId: Guid.NewGuid(),
            ExpectedDestinationContainerRevision: 1,
            DestinationSlotIndex: 0);
        var invalidCarryHandler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            transaction = new
            {
                operationId,
                operationKind = "loot_corpse_item",
                succeeded = true,
                error = (object?)null,
                characterRevisions = new[]
                {
                    new
                    {
                        characterId,
                        revision = 2,
                        carriedWeight = -1,
                        carryCapacity = 200
                    }
                },
                containerRevisions = Array.Empty<object>(),
                itemRevisions = Array.Empty<object>(),
                recoveryDeliveryIds = Array.Empty<Guid>()
            },
            characterState = CharacterStateTestData.Create(characterId, 2),
            corpse = CreateCorpseViewPayload(corpseId, characterId, "local-shard-1", 2)
        }));

        var mutation = await CreateClient(invalidCarryHandler).MutateCorpseAsync(
            Guid.NewGuid(),
            corpseId,
            mutationRequest,
            CancellationToken.None);

        Assert.False(mutation.Succeeded);
        Assert.Equal("invalid_auth_response", mutation.Error!.Code);
    }

    [Fact]
    public async Task NetworkFailureReturnsServiceUnavailable()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => throw new HttpRequestException("offline"));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("auth_service_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task TimeoutReturnsGatewayTimeout()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => throw new TaskCanceledException("timeout"));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(504, result.StatusCode);
        Assert.Equal("auth_service_timeout", result.Error!.Code);
    }

    [Fact]
    public async Task InvalidSuccessPayloadReturnsBadGateway()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.Empty,
            characterId = Guid.Empty
        }));
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task CarryStateAboveTheHardCapIsAcceptedForAnInvoluntaryOverflow()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "development-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId = Guid.NewGuid(),
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            sessionLeaseDurationMilliseconds = 30_000,
            itemStateRevision = 1,
            carriedWeight = 348,
            carryCapacity = 200,
            isReconnect = false,
            appearance = Look()
        }));

        var result = await ConsumeAsync(CreateClient(handler));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(348, result.Value!.CarriedWeight);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task APersonWithoutALookOrABotWithOneReturnsBadGateway(bool isSyntheticBot, bool withLook)
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "development-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId = Guid.NewGuid(),
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            sessionLeaseDurationMilliseconds = 30_000,
            itemStateRevision = 1,
            carriedWeight = 0,
            carryCapacity = 200,
            isReconnect = false,
            isSyntheticBot,
            appearance = withLook ? Look() : null
        }));

        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task AnAppearanceChangeUsesTheSessionRouteAndChecksTheStoredLook()
    {
        var simulationSessionId = Guid.NewGuid();
        var lookWithoutBody = false;
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(lookWithoutBody
            ? new { bodyModel = "", choices = Array.Empty<object>(), headwearLowered = true }
            : Look(headwearLowered: true)));
        var client = CreateClient(handler);
        var request = new SimulationAppearanceChangeRequest("runtime-1", "session-token", "headwear", true, null, null);

        var result = await client.ChangeAppearanceAsync(simulationSessionId, request, CancellationToken.None);
        lookWithoutBody = true;
        var invalid = await client.ChangeAppearanceAsync(simulationSessionId, request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.True(result.Value!.HeadwearLowered);
        Assert.Equal("Stubble", Assert.Single(result.Value.Choices).Option);
        Assert.False(invalid.Succeeded);
        Assert.Equal(502, invalid.StatusCode);
        Assert.Equal($"/api/simulation-sessions/{simulationSessionId}/appearance", handler.Requests[0].Path);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("headwear", body.RootElement.GetProperty("change").GetString());
        Assert.True(body.RootElement.GetProperty("headwearLowered").GetBoolean());
    }

    [Fact]
    public async Task NegativeCarriedWeightReturnsBadGateway()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "development-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId = Guid.NewGuid(),
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            sessionLeaseDurationMilliseconds = 30_000,
            itemStateRevision = 1,
            carriedWeight = -1,
            carryCapacity = 200,
            isReconnect = false
        }));

        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("invalid_auth_response", result.Error!.Code);
    }

    [Fact]
    public async Task ProblemDetailsErrorIsMappedWithoutLosingItsCode()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new
            {
                type = "about:blank",
                title = "Conflict",
                status = 409,
                detail = "The ticket belongs to another worker runtime.",
                code = "wrong_simulation_worker"
            })
        });
        var result = await ConsumeAsync(CreateClient(handler));

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("wrong_simulation_worker", result.Error!.Code);
        Assert.Equal(
            "The ticket belongs to another worker runtime.",
            result.Error.Message);
    }

    [Theory]
    [InlineData("item_slot_occupied", "item_slot_occupied")]
    [InlineData("", "auth_service_rejected")]
    [InlineData("Not-A-Code", "auth_service_rejected")]
    [InlineData("a_code_that_is_much_longer_than_the_sixty_four_character_protocol_limit", "auth_service_rejected")]
    public void ForwardedErrorCodesAlwaysFitTheRealtimeProtocol(string code, string expected)
    {
        Assert.Equal(expected, GameServiceClient.NormalizeForwardedErrorCode(code));
    }

    [Fact]
    public void ForwardedErrorMessagesAlwaysFitTheRealtimeProtocol()
    {
        Assert.Equal(
            "GameService rejected the request.",
            GameServiceClient.NormalizeForwardedErrorMessage(""));
        Assert.Equal(
            "GameService rejected the request.",
            GameServiceClient.NormalizeForwardedErrorMessage(null));
        Assert.Equal("Slot ? is taken.", GameServiceClient.NormalizeForwardedErrorMessage("Slot å is taken."));
        Assert.Equal(256, GameServiceClient.NormalizeForwardedErrorMessage(new string('x', 2_000)).Length);
        RealtimeProtocol.EncodeServerDisconnect(
            GameServiceClient.NormalizeForwardedErrorCode("Bad Code"),
            GameServiceClient.NormalizeForwardedErrorMessage(new string('å', 2_000)));
    }

    // A person's look as GameService sends it with a consumed ticket.
    private static object Look(bool headwearLowered = false)
    {
        return new
        {
            bodyModel = "Human_Male",
            choices = new[] { new { kind = "face", slot = "", option = "Stubble" } },
            headwearLowered
        };
    }

    private static Task<GameServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeAsync(
        GameServiceClient client)
    {
        return client.ConsumeJoinTicketAsync(
            "join-ticket",
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            CancellationToken.None);
    }

    private static SimulationWorkerHeartbeatRequest CreateHeartbeatRequest()
    {
        return new SimulationWorkerHeartbeatRequest(
            "local-fleet",
            "local-node-1",
            "local-shard-1",
            "development-world-1",
            "runtime-1",
            DateTime.UtcNow.AddMinutes(-1),
            "worker.example.test",
            28015,
            100,
            4,
            RealtimeProtocol.Version,
            GameSimulationCompatibility.Revision,
            "collision-revision-1",
            "item-catalog-revision-1");
    }

    private static object[] CreateCorpseSections()
    {
        return
        [
            new
            {
                sectionKind = "general_inventory",
                containerId = Guid.NewGuid(),
                containerRevision = 1,
                itemCount = 0
            },
            new
            {
                sectionKind = "equipment",
                containerId = Guid.NewGuid(),
                containerRevision = 1,
                itemCount = 0
            },
            new
            {
                sectionKind = "bag",
                containerId = Guid.NewGuid(),
                containerRevision = 1,
                itemCount = 0
            }
        ];
    }

    private static object CreateCorpseViewPayload(
        Guid corpseId,
        Guid sourceCharacterId,
        string shardId,
        long revision)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        return new
        {
            corpseId,
            sourceCharacterId,
            sourceDisplayName = "Fallen Hero",
            shardId,
            positionX = 1d,
            positionY = 2d,
            positionZ = 3d,
            presentationKey = "corpse.remains",
            revision,
            createdAt,
            expiresAt = createdAt.AddMinutes(5),
            sections = new object[]
            {
                CreateCorpseViewSection("general_inventory", "corpse_general_inventory"),
                CreateCorpseViewSection("equipment", "corpse_equipment"),
                CreateCorpseViewSection("bag", "corpse_bag_contents")
            },
            presentationSnapshots = Array.Empty<object>()
        };
    }

    private static object CreateCorpseViewSection(string sectionKind, string containerType)
    {
        return new
        {
            sectionKind,
            containerId = Guid.NewGuid(),
            containerType,
            containerRevision = 1,
            slotCapacity = 1,
            slots = new[]
            {
                new
                {
                    slotIndex = 0,
                    slotKind = "general",
                    acceptedTags = Array.Empty<string>(),
                    equipmentSlotId = string.Equals(
                        sectionKind,
                        "equipment",
                        StringComparison.Ordinal)
                        ? "head"
                        : string.Empty,
                    item = (object?)null
                }
            }
        };
    }

    private static SimulationAmmunitionConsumptionRequest CreateAmmunitionRequest()
    {
        return new SimulationAmmunitionConsumptionRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "local-simulation-worker-1",
            "runtime-1",
            "local-shard-1",
            "simulation-session-token",
            12,
            Guid.NewGuid(),
            3,
            5);
    }

    private static SimulationItemTransactionResponse CreateAmmunitionResponse(
        SimulationAmmunitionConsumptionRequest request,
        SimulationCharacterState state)
    {
        return new SimulationItemTransactionResponse(
            request.OperationId,
            "consume_equipped_ammunition",
            true,
            null,
            [new SimulationItemCharacterRevision(request.CharacterId, 13, 0, 200)],
            [],
            [new SimulationItemInstanceRevision(request.ItemInstanceId, 4)],
            [],
            state);
    }

    private static GameServiceClient CreateClient(HttpMessageHandler handler)
    {
        return new GameServiceClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://game-service.test")
        });
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                body,
                request.RequestUri.Query,
                request.Headers.TryGetValues("X-Correlation-ID", out var correlationValues)
                    ? correlationValues.Single()
                    : string.Empty));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(
        string Path,
        string Body,
        string Query,
        string CorrelationId);
}
