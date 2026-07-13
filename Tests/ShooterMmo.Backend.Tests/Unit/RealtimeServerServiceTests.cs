using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Realtime;
using WorldServer.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeServerServiceTests
{
    [Fact]
    public async Task UdpClientCanJoinMoveAndLeaveAnAuthenticatedWorldSession()
    {
        await RunRealtimeSessionAsync(invalidateAsReplaced: false);
    }

    [Fact]
    public async Task ReplacedAccountSessionReceivesTheSpecificServerDisconnectReason()
    {
        await RunRealtimeSessionAsync(invalidateAsReplaced: true);
    }

    private static async Task RunRealtimeSessionAsync(bool invalidateAsReplaced)
    {
        var port = FindAvailableUdpPort();
        var authHandler = new WorldSessionAuthHandler();
        using var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://auth-service.test")
        };
        var authClient = new AuthServiceClient(httpClient);
        var sessionStore = new ActivePlayerSessionStore();
        var config = CreateConfig(port);
        var staticCollisionWorld = CollisionTestWorldFactory.Create();
        var collisionWorld = new CompositeCollisionWorld(
            staticCollisionWorld,
            new DynamicCollisionWorld(staticCollisionWorld.ChunkSize));
        var server = new RealtimeServerService(
            config,
            new WorldJoinService(authClient, sessionStore, config),
            new WorldSessionReleaseService(authClient, sessionStore),
            sessionStore,
            new RealtimeTransportReadiness(),
            staticCollisionWorld,
            collisionWorld,
            NullLogger<RealtimeServerService>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(timeout.Token);

        var joinAccepted = new TaskCompletionSource<RealtimeJoinAccepted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveAccepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverDisconnect = new TaskCompletionSource<RealtimeError>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var movementSnapshot = new TaskCompletionSource<RealtimePlayerSnapshot>(
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

                if (messageType == RealtimeMessageType.WorldSnapshot)
                {
                    Assert.Equal(RealtimeProtocol.UnreliableReceiveChannel, channel);
                    Assert.Equal(DeliveryMethod.Unreliable, deliveryMethod);
                    Assert.True(RealtimeProtocol.TryDecodeWorldSnapshot(
                        packet,
                        out var snapshot,
                        out var error), error);
                    var player = snapshot.Players.SingleOrDefault(
                        value => value.CharacterId == authHandler.CharacterId.ToString("D"));
                    if (player is not null
                        && player.LastProcessedInputSequence == 1
                        && player.State.PositionZ > -1f
                        && movementSnapshot.TrySetResult(player))
                    {
                        if (invalidateAsReplaced)
                        {
                            Assert.True(sessionStore.Invalidate(
                                authHandler.CharacterId,
                                authHandler.WorldSessionId,
                                authHandler.WorldSessionToken,
                                "account_session_replaced",
                                "This account logged in from another client."));
                        }
                        else
                        {
                            var joined = joinAccepted.Task.GetAwaiter().GetResult();
                            peer.Send(
                                RealtimeProtocol.EncodeLeaveRequest(joined.WorldSessionId),
                                DeliveryMethod.ReliableOrdered);
                        }
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
            var movedPlayer = await movementSnapshot.Task.WaitAsync(timeout.Token);
            await completion.WaitAsync(timeout.Token);

            Assert.Equal(authHandler.CharacterId.ToString("D"), joined.CharacterId);
            Assert.Equal(staticCollisionWorld.Revision, joined.CollisionRevision);
            Assert.Equal(0.35f, joined.MovementSettings.CharacterRadius);
            Assert.Equal(55f, joined.MovementSettings.MaximumFallSpeed);
            Assert.Equal(1u, movedPlayer.LastProcessedInputSequence);
            Assert.Empty(sessionStore.ListActiveSessions());
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

    private static WorldServerConfig CreateConfig(int port)
    {
        return new WorldServerConfig(
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
            "test-world-server-secret-at-least-32-characters",
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

    private sealed class WorldSessionAuthHandler : HttpMessageHandler
    {
        public Guid AccountId { get; } = Guid.NewGuid();

        public Guid CharacterId { get; } = Guid.NewGuid();

        public Guid WorldSessionId { get; } = Guid.NewGuid();

        public string WorldSessionToken { get; } = "exact-world-session-token";

        public int ReleaseCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/world-join-tickets/consume")
            {
                return Task.FromResult(JsonResponse(new ConsumedJoinTicketResponse(
                    AccountId,
                    CharacterId,
                    "Network Hero",
                    "local-world-1",
                    WorldSessionId,
                    WorldSessionToken,
                    DateTime.UtcNow.AddSeconds(30),
                    false)));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            {
                ReleaseCount++;
                return Task.FromResult(JsonResponse(new WorldSessionLeaseResponse(
                    Guid.Parse(request.RequestUri.Segments[^2].TrimEnd('/')),
                    CharacterId,
                    "local-world-1",
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
