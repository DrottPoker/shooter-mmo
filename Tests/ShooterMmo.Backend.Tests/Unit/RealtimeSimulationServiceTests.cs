using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Entities;
using SimulationWorker.Realtime;
using SimulationWorker.Registry;
using SimulationWorker.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeSimulationServiceTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void LeaveInProgressOwnsCleanupAfterLocalLeaseRemoval(
        bool leaveStarted,
        bool isCurrentSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            RealtimeSimulationService.ShouldEnforceSessionInvalidation(
                leaveStarted,
                isCurrentSession));
    }

    [Fact]
    public async Task UdpClientCanJoinMoveAndLeaveAnAuthenticatedSimulationSession()
    {
        await RunRealtimeSessionAsync(invalidateAsReplaced: false, isSyntheticBot: false);
    }

    [Fact]
    public async Task ReplacedAccountSessionReceivesTheSpecificServerDisconnectReason()
    {
        await RunRealtimeSessionAsync(invalidateAsReplaced: true, isSyntheticBot: false);
    }

    [Fact]
    public async Task SyntheticBotJoinIsSeparatedFromRealPlayerPopulation()
    {
        await RunRealtimeSessionAsync(invalidateAsReplaced: false, isSyntheticBot: true);
    }

    private static async Task RunRealtimeSessionAsync(
        bool invalidateAsReplaced,
        bool isSyntheticBot)
    {
        var port = FindAvailableUdpPort();
        var authHandler = new SimulationSessionAuthHandler(isSyntheticBot);
        using var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        };
        var authClient = new AuthServiceClient(httpClient);
        var sessionStore = new ActiveSimulationSessionStore();
        var config = CreateConfig(port);
        var identity = new SimulationWorkerIdentity("test-runtime", DateTime.UtcNow.AddMinutes(-1));
        var staticCollisionWorld = CollisionTestWorldFactory.Create();
        var collisionWorld = new CompositeCollisionWorld(
            staticCollisionWorld,
            new DynamicCollisionWorld(staticCollisionWorld.ChunkSize));
        using var networkMetrics = new RealtimeNetworkMetrics();
        var server = new RealtimeSimulationService(
            config,
            new SimulationJoinService(authClient, sessionStore, config, identity),
            new SimulationSessionReleaseService(authClient, sessionStore),
            sessionStore,
            new SimulationEntityRegistry(),
            new ConnectionEntityBindingRegistry(),
            new RealtimeTransportReadiness(),
            staticCollisionWorld,
            collisionWorld,
            NullLogger<RealtimeSimulationService>.Instance,
            providedNetworkMetrics: networkMetrics);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(timeout.Token);

        var joinAccepted = new TaskCompletionSource<RealtimeJoinAccepted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveAccepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverDisconnect = new TaskCompletionSource<RealtimeError>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entitySpawn = new TaskCompletionSource<RealtimeEntitySpawn>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var movementSnapshot = new TaskCompletionSource<RealtimeEntitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new EventBasedNetListener();
        var client = new NetManager(listener)
        {
            ChannelsCount = RealtimeProtocol.ChannelCount
        };

        listener.PeerConnectedEvent += peer =>
        {
            peer.Send(
                RealtimeProtocol.EncodeJoinRequest("test-join-ticket"),
                DeliveryMethod.ReliableOrdered);
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            try
            {
                var packet = reader.GetRemainingBytes();
                Assert.True(RealtimeProtocol.TryReadMessageType(packet, out var messageType));

                if (messageType == RealtimeMessageType.JoinAccepted)
                {
                    Assert.True(RealtimeProtocol.TryDecodeJoinAccepted(
                        packet,
                        out var session,
                        out var error), error);
                    var population = networkMetrics.Capture();
                    Assert.Equal(isSyntheticBot ? 0 : 1, population.ActiveRealPlayers);
                    Assert.Equal(isSyntheticBot ? 1 : 0, population.ActiveSyntheticBots);
                    Assert.Equal(0, population.UnauthenticatedPeers);
                    joinAccepted.TrySetResult(session);
                    peer.Send(
                        RealtimeProtocol.EncodeMovementInputBatch(new[]
                        {
                            new RealtimeMovementInput(
                                1,
                                1,
                                0f,
                                1f,
                                0f,
                                RealtimeMovementButtons.None)
                        }),
                        RealtimeProtocol.MovementInputChannel,
                        DeliveryMethod.Sequenced);
                    return;
                }

                if (messageType == RealtimeMessageType.SimulationSnapshot)
                {
                    Assert.Equal(RealtimeProtocol.UnreliableReceiveChannel, channel);
                    Assert.Equal(DeliveryMethod.Unreliable, deliveryMethod);
                    Assert.True(RealtimeProtocol.TryDecodeSimulationSnapshot(
                        packet,
                        out var snapshot,
                        out var error), error);
                    var joined = joinAccepted.Task.GetAwaiter().GetResult();
                    var entity = snapshot.Entities.SingleOrDefault(
                        value => value.EntityId == joined.ControlledEntityId);
                    if (entity is not null
                        && entity.LastProcessedInputSequence == 1
                        && entity.State.PositionZ > -1f
                        && movementSnapshot.TrySetResult(entity))
                    {
                        if (invalidateAsReplaced)
                        {
                            Assert.True(sessionStore.Invalidate(
                                authHandler.CharacterId,
                                authHandler.SimulationSessionId,
                                authHandler.SimulationSessionToken,
                                "account_session_replaced",
                                "This account logged in from another client."));
                        }
                        else
                        {
                            peer.Send(
                                RealtimeProtocol.EncodeLeaveRequest(joined.SimulationSessionId),
                                DeliveryMethod.ReliableOrdered);
                        }
                    }

                    return;
                }

                if (messageType == RealtimeMessageType.EntitySpawn)
                {
                    Assert.Equal(RealtimeProtocol.ControlChannel, channel);
                    Assert.Equal(DeliveryMethod.ReliableOrdered, deliveryMethod);
                    Assert.True(RealtimeProtocol.TryDecodeEntitySpawn(
                        packet,
                        out var spawn,
                        out var error), error);
                    if (spawn.PersistentId == authHandler.CharacterId.ToString("D"))
                    {
                        entitySpawn.TrySetResult(spawn);
                    }

                    return;
                }

                if (messageType == RealtimeMessageType.LeaveAccepted)
                {
                    Assert.True(RealtimeProtocol.TryDecodeLeaveAccepted(packet, out var error), error);
                    leaveAccepted.TrySetResult();
                    return;
                }

                if (messageType == RealtimeMessageType.ServerDisconnect)
                {
                    Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(
                        packet,
                        out var reason,
                        out var error), error);
                    serverDisconnect.TrySetResult(reason);
                }
            }
            catch (Exception exception)
            {
                joinAccepted.TrySetException(exception);
                entitySpawn.TrySetException(exception);
                movementSnapshot.TrySetException(exception);
                leaveAccepted.TrySetException(exception);
                serverDisconnect.TrySetException(exception);
            }
            finally
            {
                reader.Recycle();
            }
        };

        try
        {
            Assert.True(client.Start());
            client.Connect("127.0.0.1", port, RealtimeProtocol.ConnectionKey);

            var completion = invalidateAsReplaced
                ? serverDisconnect.Task
                : leaveAccepted.Task;
            while (!completion.IsCompleted)
            {
                client.PollEvents();
                await Task.Delay(10, timeout.Token);
            }

            var joined = await joinAccepted.Task.WaitAsync(timeout.Token);
            var spawned = await entitySpawn.Task.WaitAsync(timeout.Token);
            var movedPlayer = await movementSnapshot.Task.WaitAsync(timeout.Token);
            await completion.WaitAsync(timeout.Token);

            Assert.Equal(authHandler.CharacterId.ToString("D"), joined.CharacterId);
            Assert.NotEqual(0ul, joined.ControlledEntityId);
            Assert.Equal(joined.ControlledEntityId, spawned.EntityId);
            Assert.Equal("player.default", spawned.ArchetypeId);
            Assert.Equal(staticCollisionWorld.Revision, joined.CollisionRevision);
            Assert.Equal(0.35f, joined.MovementSettings.CharacterRadius);
            Assert.Equal(55f, joined.MovementSettings.MaximumFallSpeed);
            Assert.Equal(1u, movedPlayer.LastProcessedInputSequence);
            Assert.Empty(sessionStore.ListActiveSessions());
            var finalPopulation = networkMetrics.Capture();
            Assert.Equal(0, finalPopulation.ActiveRealPlayers);
            Assert.Equal(0, finalPopulation.ActiveSyntheticBots);
            if (invalidateAsReplaced)
            {
                var reason = await serverDisconnect.Task.WaitAsync(timeout.Token);
                Assert.Equal("account_session_replaced", reason.Code);
                Assert.Equal("This account logged in from another client.", reason.Message);
                Assert.Equal(0, authHandler.ReleaseCount);
            }
            else
            {
                await leaveAccepted.Task.WaitAsync(timeout.Token);
                Assert.Equal(1, authHandler.ReleaseCount);
            }
        }
        finally
        {
            client.Stop();
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    private static SimulationWorkerConfig CreateConfig(int port)
    {
        return new SimulationWorkerConfig(
            "local-simulation-worker-1",
            "local-fleet",
            "local-node-1",
            "local-shard-1",
            "local-world-1",
            "CollisionData",
            port,
            "127.0.0.1",
            port,
            4,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(5),
            15,
            TimeSpan.FromMilliseconds(500),
            new MovementSimulationSettings(
                30,
                5f,
                8f,
                720f,
                -24f,
                7f,
                -2f,
                0f,
                -14f,
                14f,
                -14f,
                14f),
            new MovementSpawnConfig(0f, 0f, -1f, 0f),
            new Uri("http://auth-service.test"),
            TimeSpan.FromSeconds(2),
            "test-simulation-worker-secret-at-least-32-characters",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class SimulationSessionAuthHandler(bool isSyntheticBot) : HttpMessageHandler
    {
        public Guid AccountId { get; } = Guid.NewGuid();

        public Guid CharacterId { get; } = Guid.NewGuid();

        public Guid SimulationSessionId { get; } = Guid.NewGuid();

        public string SimulationSessionToken { get; } = "exact-simulation-session-token";

        public int ReleaseCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/simulation-join-tickets/consume")
            {
                return Task.FromResult(JsonResponse(new ConsumedSimulationJoinTicketResponse(
                    AccountId,
                    CharacterId,
                    "Network Hero",
                    "local-shard-1",
                    "local-world-1",
                    "local-simulation-worker-1",
                    "test-runtime",
                    SimulationSessionId,
                    SimulationSessionToken,
                    DateTime.UtcNow.AddSeconds(30),
                    false)
                {
                    IsSyntheticBot = isSyntheticBot
                }));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            {
                ReleaseCount++;
                return Task.FromResult(JsonResponse(new SimulationSessionLeaseResponse(
                    Guid.Parse(request.RequestUri.Segments[^2].TrimEnd('/')),
                    CharacterId,
                    "local-shard-1",
                    "local-simulation-worker-1",
                    "test-runtime",
                    DateTime.UtcNow,
                    true)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value)
            };
        }
    }
}
