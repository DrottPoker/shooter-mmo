using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class AuthServiceClientTests
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
            worldId = "local-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId,
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            itemStateRevision = 12,
            carriedWeight = 210,
            carryCapacity = 200,
            isReconnect = false
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
            secureContainerEntitlementRevision = (long?)null
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
            "corpse.generic_loot_crate");
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
                presentationKey = "corpse.generic_loot_crate",
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
            recoveryDeliveryIds = Array.Empty<Guid>()
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
            worldId = "local-world-1",
            host = requestBody.Host,
            udpPort = requestBody.UdpPort,
            maxConnections = requestBody.MaxConnections,
            activeConnections = requestBody.ActiveConnections,
            protocolVersion = requestBody.ProtocolVersion,
            simulationRevision = requestBody.SimulationRevision,
            collisionRevision = requestBody.CollisionRevision,
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
                    presentationKey = "corpse.generic_loot_crate",
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
                    presentationKey = "corpse.generic_loot_crate",
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
                recoveryDeliveryIds = Array.Empty<Guid>(),
                secureContainerEntitlementRevision = (long?)null
            },
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

    [Fact]
    public async Task CorpseClientRejectsCrossShardViewAndOverHardCapMutation()
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
        var hardCapHandler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
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
                        carriedWeight = 281,
                        carryCapacity = 200
                    }
                },
                containerRevisions = Array.Empty<object>(),
                itemRevisions = Array.Empty<object>(),
                recoveryDeliveryIds = Array.Empty<Guid>(),
                secureContainerEntitlementRevision = (long?)null
            },
            corpse = CreateCorpseViewPayload(corpseId, characterId, "local-shard-1", 2)
        }));

        var mutation = await CreateClient(hardCapHandler).MutateCorpseAsync(
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
    public async Task CarryStateAboveTheHardCapReturnsBadGateway()
    {
        var handler = new RecordingHttpMessageHandler(_ => CreateJsonResponse(new
        {
            accountId = Guid.NewGuid(),
            characterId = Guid.NewGuid(),
            characterName = "Hero One",
            shardId = "local-shard-1",
            worldId = "local-world-1",
            workerId = "local-simulation-worker-1",
            workerRuntimeId = "runtime-1",
            simulationSessionId = Guid.NewGuid(),
            simulationSessionToken = "simulation-session-token",
            sessionExpiresAt = DateTime.UtcNow.AddMinutes(1),
            itemStateRevision = 1,
            carriedWeight = 281,
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

    private static Task<AuthServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeAsync(
        AuthServiceClient client)
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
            "runtime-1",
            DateTime.UtcNow.AddMinutes(-1),
            "worker.example.test",
            28015,
            100,
            4,
            RealtimeProtocol.Version,
            GameSimulationCompatibility.Revision,
            "collision-revision-1");
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
            presentationKey = "corpse.generic_loot_crate",
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
                    item = (object?)null
                }
            }
        };
    }

    private static AuthServiceClient CreateClient(HttpMessageHandler handler)
    {
        return new AuthServiceClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://auth-service.test")
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
                request.RequestUri.Query));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(string Path, string Body, string Query);
}
