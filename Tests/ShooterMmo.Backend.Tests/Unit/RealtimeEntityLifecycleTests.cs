using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;
using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using WorldServer.Auth;
using WorldServer.Config;
using WorldServer.Entities;
using WorldServer.Realtime;
using WorldServer.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeEntityLifecycleTests
{
    [Fact]
    public async Task ExistingPeerReceivesReliableSpawnAndDespawnForAnotherPlayer()
    {
        var port = FindAvailableUdpPort();
        var firstTicket = new TicketSession("ticket-one", "First Hero");
        var secondTicket = new TicketSession("ticket-two", "Second Hero");
        using var httpClient = new HttpClient(new MultipleWorldSessionAuthHandler(
            firstTicket,
            secondTicket))
        {
            BaseAddress = new Uri("http://auth-service.test")
        };
        var sessionStore = new ActivePlayerSessionStore();
        var config = CreateConfig(port);
        var staticCollisionWorld = CollisionTestWorldFactory.Create();
        var server = new RealtimeServerService(
            config,
            new WorldJoinService(new AuthServiceClient(httpClient), sessionStore, config),
            new WorldSessionReleaseService(new AuthServiceClient(httpClient), sessionStore),
            sessionStore,
            new WorldEntityRegistry(),
            new ConnectionEntityBindingRegistry(),
            new RealtimeTransportReadiness(),
            staticCollisionWorld,
            new CompositeCollisionWorld(
                staticCollisionWorld,
                new DynamicCollisionWorld(staticCollisionWorld.ChunkSize)),
            NullLogger<RealtimeServerService>.Instance);
        using var firstClient = new RealtimeTestClient(firstTicket.Ticket);
        using var secondClient = new RealtimeTestClient(secondTicket.Ticket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await server.StartAsync(timeout.Token);

        try
        {
            firstClient.Start(port);
            await PollUntilAsync(
                () => firstClient.Joined.IsCompletedSuccessfully
                    && firstClient.Spawns.Any(spawn =>
                        spawn.PersistentId == firstTicket.CharacterId.ToString("D")),
                timeout.Token,
                firstClient);
            var firstJoin = await firstClient.Joined.WaitAsync(timeout.Token);

            secondClient.Start(port);
            await PollUntilAsync(
                () => secondClient.Joined.IsCompletedSuccessfully,
                timeout.Token,
                firstClient,
                secondClient);
            var secondJoin = await secondClient.Joined.WaitAsync(timeout.Token);

            await PollUntilAsync(
                () => firstClient.Spawns.Any(spawn =>
                        spawn.EntityId == secondJoin.ControlledEntityId)
                    && secondClient.Spawns.Any(spawn =>
                        spawn.EntityId == firstJoin.ControlledEntityId),
                timeout.Token,
                firstClient,
                secondClient);

            secondClient.SendLeave(secondJoin.WorldSessionId);
            await PollUntilAsync(
                () => secondClient.LeaveAccepted.IsCompletedSuccessfully
                    && firstClient.Despawns.Any(despawn =>
                        despawn.EntityId == secondJoin.ControlledEntityId),
                timeout.Token,
                firstClient,
                secondClient);

            var despawn = Assert.Single(
                firstClient.Despawns,
                value => value.EntityId == secondJoin.ControlledEntityId);
            Assert.Equal("left_world", despawn.Reason);
            Assert.NotEqual(firstJoin.ControlledEntityId, secondJoin.ControlledEntityId);
        }
        finally
        {
            firstClient.Stop();
            secondClient.Stop();
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    private static async Task PollUntilAsync(
        Func<bool> completed,
        CancellationToken cancellationToken,
        params RealtimeTestClient[] clients)
    {
        while (!completed())
        {
            foreach (var client in clients)
            {
                client.PollEvents();
                client.ThrowIfFailed();
            }

            await Task.Delay(10, cancellationToken);
        }

        foreach (var client in clients)
        {
            client.PollEvents();
            client.ThrowIfFailed();
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

    private sealed class RealtimeTestClient : IDisposable
    {
        private readonly EventBasedNetListener listener = new();
        private readonly NetManager client;
        private readonly string ticket;
        private NetPeer? peer;
        private Exception? failure;

        public RealtimeTestClient(string ticket)
        {
            this.ticket = ticket;
            client = new NetManager(listener)
            {
                ChannelsCount = RealtimeProtocol.ChannelCount
            };
            listener.PeerConnectedEvent += OnPeerConnected;
            listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public Task<RealtimeJoinAccepted> Joined => joined.Task;

        public Task LeaveAccepted => leaveAccepted.Task;

        public List<RealtimeEntitySpawn> Spawns { get; } = [];

        public List<RealtimeEntityDespawn> Despawns { get; } = [];

        private readonly TaskCompletionSource<RealtimeJoinAccepted> joined = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource leaveAccepted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(int port)
        {
            Assert.True(client.Start());
            client.Connect("127.0.0.1", port, RealtimeProtocol.ConnectionKey);
        }

        public void SendLeave(string worldSessionId)
        {
            Assert.NotNull(peer);
            peer.Send(
                RealtimeProtocol.EncodeLeaveRequest(worldSessionId),
                DeliveryMethod.ReliableOrdered);
        }

        public void PollEvents()
        {
            client.PollEvents();
        }

        public void ThrowIfFailed()
        {
            if (failure is not null)
            {
                throw new InvalidOperationException("Realtime test client failed.", failure);
            }
        }

        public void Stop()
        {
            client.Stop();
        }

        public void Dispose()
        {
            client.Stop();
        }

        private void OnPeerConnected(NetPeer connectedPeer)
        {
            peer = connectedPeer;
            connectedPeer.Send(
                RealtimeProtocol.EncodeJoinRequest(ticket),
                DeliveryMethod.ReliableOrdered);
        }

        private void OnNetworkReceive(
            NetPeer connectedPeer,
            NetPacketReader reader,
            byte channel,
            DeliveryMethod deliveryMethod)
        {
            try
            {
                var packet = reader.GetRemainingBytes();
                Assert.True(RealtimeProtocol.TryReadMessageType(packet, out var messageType));
                if (messageType == RealtimeMessageType.WorldSnapshot)
                {
                    return;
                }

                Assert.Equal(RealtimeProtocol.ControlChannel, channel);
                Assert.Equal(DeliveryMethod.ReliableOrdered, deliveryMethod);
                switch (messageType)
                {
                    case RealtimeMessageType.JoinAccepted:
                        Assert.True(RealtimeProtocol.TryDecodeJoinAccepted(
                            packet,
                            out var accepted,
                            out var joinError), joinError);
                        joined.TrySetResult(accepted);
                        break;
                    case RealtimeMessageType.EntitySpawn:
                        Assert.True(RealtimeProtocol.TryDecodeEntitySpawn(
                            packet,
                            out var spawn,
                            out var spawnError), spawnError);
                        Spawns.Add(spawn);
                        break;
                    case RealtimeMessageType.EntityDespawn:
                        Assert.True(RealtimeProtocol.TryDecodeEntityDespawn(
                            packet,
                            out var despawn,
                            out var despawnError), despawnError);
                        Despawns.Add(despawn);
                        break;
                    case RealtimeMessageType.LeaveAccepted:
                        Assert.True(RealtimeProtocol.TryDecodeLeaveAccepted(
                            packet,
                            out var leaveError), leaveError);
                        leaveAccepted.TrySetResult();
                        break;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                reader.Recycle();
            }
        }
    }

    private sealed record TicketSession(
        string Ticket,
        string CharacterName)
    {
        public Guid AccountId { get; } = Guid.NewGuid();

        public Guid CharacterId { get; } = Guid.NewGuid();

        public Guid WorldSessionId { get; } = Guid.NewGuid();

        public string WorldSessionToken { get; } = Guid.NewGuid().ToString("N");
    }

    private sealed class MultipleWorldSessionAuthHandler(
        params TicketSession[] sessions) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, TicketSession> sessionsByTicket =
            sessions.ToDictionary(session => session.Ticket, StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<Guid, TicketSession> sessionsById =
            sessions.ToDictionary(session => session.WorldSessionId);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/world-join-tickets/consume")
            {
                var consume = await request.Content!.ReadFromJsonAsync<ConsumeJoinTicketRequest>(
                    cancellationToken);
                Assert.NotNull(consume);
                Assert.True(sessionsByTicket.TryGetValue(consume.Ticket, out var session));
                return JsonResponse(new ConsumedJoinTicketResponse(
                    session.AccountId,
                    session.CharacterId,
                    session.CharacterName,
                    "local-world-1",
                    session.WorldSessionId,
                    session.WorldSessionToken,
                    DateTime.UtcNow.AddSeconds(30),
                    false));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            {
                var worldSessionId = Guid.Parse(request.RequestUri.Segments[^2].TrimEnd('/'));
                Assert.True(sessionsById.TryGetValue(worldSessionId, out var session));
                return JsonResponse(new WorldSessionLeaseResponse(
                    worldSessionId,
                    session.CharacterId,
                    "local-world-1",
                    DateTime.UtcNow,
                    true));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
