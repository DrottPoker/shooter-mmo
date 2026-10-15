using LiteNetLib;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Registry;

namespace Mmorpg.Backend.Tests.Unit;

// Characterizes every reachable protocol rejection over real UDP, so the dispatch refactor
// keeps the codes, the messages and the delayed disconnect exactly as they are.
public sealed class RealtimeProtocolRejectionTests
{
    public static TheoryData<string, string> UnauthenticatedRejections => new()
    {
        { "invalid_header", "invalid_packet" },
        { "movement_before_join", "join_required" },
        { "join_on_movement_channel", "join_required" },
        { "join_unordered", "join_required" },
        { "invalid_join_payload", "invalid_join_request" },
        { "server_message_before_join", "join_required" }
    };

    public static TheoryData<string, string> AuthenticatedRejections => new()
    {
        { "invalid_header", "invalid_packet" },
        { "movement_on_control_channel", "invalid_movement_delivery" },
        { "invalid_movement_payload", "invalid_movement_input" },
        { "item_unordered", "invalid_item_operation_delivery" },
        { "item_without_carry_state", "carry_state_missing" },
        { "invalid_item_payload", "invalid_item_operation" },
        { "corpse_unordered", "invalid_corpse_operation_delivery" },
        { "corpse_without_carry_state", "carry_state_missing" },
        { "invalid_corpse_payload", "invalid_corpse_operation" },
        { "world_unordered", "invalid_world_interaction_delivery" },
        { "world_without_authority", "world_interaction_unavailable" },
        { "second_join", "unexpected_message" },
        { "server_message", "unexpected_message" },
        { "leave_unordered", "unexpected_message" },
        { "leave_without_guid", "internal_error" }
    };

    [Theory]
    [MemberData(nameof(UnauthenticatedRejections))]
    public async Task UnauthenticatedPeerIsRejectedWithTheExpectedCode(
        string scenario,
        string expectedCode)
    {
        await using var harness = RealtimeServerHarness.Create();
        await harness.StartAsync();
        var client = harness.Connect("unauthenticated");
        await harness.PollUntilAsync(() => client.IsConnected, "the client to connect");

        switch (scenario)
        {
            case "invalid_header":
                client.Send([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
                break;
            case "movement_before_join":
                client.SendMovement(1, 0f, 1f);
                break;
            case "join_on_movement_channel":
                client.Send(
                    RealtimeProtocol.EncodeJoinRequest("ticket"),
                    DeliveryMethod.ReliableOrdered,
                    RealtimeProtocol.MovementInputChannel);
                break;
            case "join_unordered":
                client.Send(
                    RealtimeProtocol.EncodeJoinRequest("ticket"),
                    DeliveryMethod.ReliableUnordered);
                break;
            case "invalid_join_payload":
                client.Send(WithTrailingByte(RealtimeProtocol.EncodeJoinRequest("ticket")));
                break;
            case "server_message_before_join":
                client.Send(RealtimeProtocol.EncodeLeaveAccepted());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        await AssertRejectedAsync(harness, client, expectedCode, expectLastReliable: true);
    }

    [Fact]
    public async Task SecondJoinRequestWhileTheFirstIsPendingIsRejected()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Pending Hero");
        var gate = new TestGate();
        harness.GameService.OnConsume = async (request, _) =>
        {
            await gate.WaitAsync();
            return harness.GameService.DefaultConsume(request);
        };
        await harness.StartAsync();
        try
        {
            var client = harness.Connect("pending", character.Ticket);
            await harness.PollUntilAsync(() => gate.HasEntered, "the join to start");

            client.SendJoin(character.Ticket);

            await AssertRejectedAsync(
                harness,
                client,
                "join_required",
                expectLastReliable: true);
            Assert.Equal(
                "A single join request must complete before other messages are accepted.",
                client.ServerDisconnect!.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task JoinIsRejectedWhileTheRegistrationLeaseIsInvalid()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var lease = new SimulationWorkerRegistrationLease(time);
        lease.Renew(time.GetUtcNow(), TimeSpan.FromMinutes(1));
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            RegistrationLease = lease
        });
        var character = harness.AddCharacter("Lease Hero");
        await harness.StartAsync();
        var client = harness.Connect("lease");
        await harness.PollUntilAsync(() => client.IsConnected, "the client to connect");

        time.Advance(TimeSpan.FromMinutes(2));
        client.SendJoin(character.Ticket);

        await AssertRejectedAsync(
            harness,
            client,
            "worker_registry_lease_unavailable",
            expectLastReliable: true);
        Assert.Equal(0, harness.GameService.ConsumeCount);
    }

    [Fact]
    public async Task InboundQuotaRejectsTheExcessPacket()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            UdpQuotas = UdpQuotaConfig.Default with
            {
                InboundPacketsPerSecond = 1,
                InboundPacketBurst = 1
            }
        });
        var character = harness.AddCharacter("Quota Hero");
        var gate = new TestGate();
        harness.GameService.OnConsume = async (request, _) =>
        {
            await gate.WaitAsync();
            return harness.GameService.DefaultConsume(request);
        };
        await harness.StartAsync();
        try
        {
            var client = harness.Connect("quota");
            await harness.PollUntilAsync(() => client.IsConnected, "the client to connect");

            // The pending join spends the only token, so the next packet exceeds the quota.
            client.SendJoin(character.Ticket);
            client.SendLeave(character.SimulationSessionId.ToString("D"));

            await AssertRejectedAsync(
                harness,
                client,
                "udp_rate_limited",
                expectLastReliable: true);
            Assert.Equal(1, harness.NetworkMetrics!.Capture().QuotaRejectedPackets);
        }
        finally
        {
            gate.Release();
        }
    }

    [Theory]
    [MemberData(nameof(AuthenticatedRejections))]
    public async Task AuthenticatedPeerIsRejectedWithTheExpectedCode(
        string scenario,
        string expectedCode)
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Protocol Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        switch (scenario)
        {
            case "invalid_header":
                client.Send([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
                break;
            case "movement_on_control_channel":
                client.Send(RealtimeProtocol.EncodeMovementInputBatch([
                    new RealtimeMovementInput(1, 1, 0f, 1f, 0f, RealtimeMovementButtons.None)
                ]));
                break;
            case "invalid_movement_payload":
                client.Send(
                    WithTrailingByte(RealtimeProtocol.EncodeMovementInputBatch([
                        new RealtimeMovementInput(1, 1, 0f, 1f, 0f, RealtimeMovementButtons.None)
                    ])),
                    DeliveryMethod.Sequenced,
                    RealtimeProtocol.MovementInputChannel);
                break;
            case "item_unordered":
                client.Send(
                    RealtimeProtocol.EncodeItemOperationIntent(CreateItemIntent()),
                    DeliveryMethod.ReliableUnordered);
                break;
            case "item_without_carry_state":
                Assert.True(harness.CarryStateStore.Remove(
                    character.CharacterId,
                    character.SimulationSessionId));
                client.SendItemIntent(CreateItemIntent());
                break;
            case "invalid_item_payload":
                client.Send(WithTrailingByte(
                    RealtimeProtocol.EncodeItemOperationIntent(CreateItemIntent())));
                break;
            case "corpse_unordered":
                client.Send(
                    RealtimeProtocol.EncodeCorpseInteractionIntent(CreateCorpseIntent()),
                    DeliveryMethod.ReliableUnordered);
                break;
            case "corpse_without_carry_state":
                Assert.True(harness.CarryStateStore.Remove(
                    character.CharacterId,
                    character.SimulationSessionId));
                client.SendCorpseIntent(CreateCorpseIntent());
                break;
            case "invalid_corpse_payload":
                client.Send(WithTrailingByte(
                    RealtimeProtocol.EncodeCorpseInteractionIntent(CreateCorpseIntent())));
                break;
            case "world_unordered":
                client.Send(
                    RealtimeProtocol.EncodeWorldInteractionIntent(
                        CreateWorldIntent(character.SimulationSessionId)),
                    DeliveryMethod.ReliableUnordered);
                break;
            case "world_without_authority":
                client.SendWorldIntent(CreateWorldIntent(character.SimulationSessionId));
                break;
            case "second_join":
                client.SendJoin(character.Ticket);
                break;
            case "server_message":
                client.Send(RealtimeProtocol.EncodeLeaveAccepted());
                break;
            case "leave_without_guid":
                // The decoded leave has no error text, so encoding its rejection throws
                // and the packet failure path disconnects the peer.
                client.SendLeave("not-a-guid");
                break;
            case "leave_unordered":
                client.Send(
                    RealtimeProtocol.EncodeLeaveRequest(
                        character.SimulationSessionId.ToString("D")),
                    DeliveryMethod.ReliableUnordered);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        await AssertRejectedAsync(harness, client, expectedCode);
        if (expectedCode == "internal_error")
        {
            Assert.Contains(
                harness.Logger.Entries,
                entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
                    && entry.Template == "[SIMULATION] Could not process a realtime packet from UDP peer {PeerId}."
                    && entry.Exception is ArgumentException);
        }
    }

    [Fact]
    public async Task InvalidWorldInteractionPayloadIsRejectedWhenAuthorityIsActive()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true
        });
        var character = harness.AddCharacter("World Protocol Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(WithTrailingByte(RealtimeProtocol.EncodeWorldInteractionIntent(
            CreateWorldIntent(character.SimulationSessionId))));

        await AssertRejectedAsync(harness, client, "invalid_world_interaction");
    }

    [Fact]
    public async Task SecondLeaveWhileTheFirstIsPendingIsRejected()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Leaving Hero");
        var gate = new TestGate();
        harness.GameService.OnRelease = async (sessionId, _, _) =>
        {
            await gate.WaitAsync();
            return harness.GameService.DefaultRelease(sessionId);
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            client.SendLeave(character.SimulationSessionId.ToString("D"));
            await harness.PollUntilAsync(() => gate.HasEntered, "the leave to start");

            client.SendLeave(character.SimulationSessionId.ToString("D"));

            await AssertRejectedAsync(harness, client, "unexpected_message");
            Assert.Equal(
                "The message is not valid for the current connection state.",
                client.ServerDisconnect!.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    [Theory]
    [InlineData("invalid_payload", "invalid_leave_request")]
    [InlineData("other_session", "simulation_session_changed")]
    public async Task InvalidLeaveIsRejectedWithoutDisconnecting(
        string scenario,
        string expectedCode)
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Leave Protocol Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(scenario == "other_session"
            ? RealtimeProtocol.EncodeLeaveRequest(Guid.NewGuid().ToString("D"))
            : WithTrailingByte(RealtimeProtocol.EncodeLeaveRequest(
                character.SimulationSessionId.ToString("D"))));
        await harness.PollUntilAsync(
            () => client.LeaveRejections.Count == 1,
            "the leave rejection");

        var rejection = Assert.Single(client.LeaveRejections);
        Assert.Equal(expectedCode, rejection.Code);
        if (expectedCode == "simulation_session_changed")
        {
            Assert.Equal(
                "The active simulation session no longer matches the leave request.",
                rejection.Message);
        }

        client.SendLeave(character.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(
            () => client.Has(RealtimeMessageType.LeaveAccepted),
            "the valid leave");
        Assert.Null(client.ServerDisconnect);
    }

    // A rejected peer keeps its session until the delayed disconnect, so broadcasts such as
    // corpse presence may still follow the ServerDisconnect. Only a peer without a bound
    // session is guaranteed to receive the ServerDisconnect as its last reliable message.
    internal static async Task AssertRejectedAsync(
        RealtimeServerHarness harness,
        RealtimeRecordingClient client,
        string expectedCode,
        bool expectLastReliable = false)
    {
        await harness.PollUntilAsync(
            () => client.ServerDisconnect is not null && client.IsDisconnected,
            $"{client.Name} to be rejected with {expectedCode}");
        var reliable = client.ReliablePackets;
        var disconnect = Assert.Single(
            reliable,
            packet => packet.Type == RealtimeMessageType.ServerDisconnect);
        if (expectLastReliable)
        {
            Assert.Equal(RealtimeMessageType.ServerDisconnect, reliable[^1].Type);
        }

        Assert.Equal(expectedCode, client.ServerDisconnect!.Code);
        Assert.True(
            client.DisconnectedAtUtc!.Value - disconnect.ReceivedAtUtc
                >= TimeSpan.FromMilliseconds(150),
            "The server must keep the peer for the graceful disconnect delay.");
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Template == "[SIMULATION] Disconnecting UDP peer {PeerId} with code {Code}: {Message}"
                && entry.Message.Contains(expectedCode, StringComparison.Ordinal));
    }

    internal static byte[] WithTrailingByte(byte[] packet)
    {
        return [.. packet, 0x00];
    }

    internal static RealtimeItemOperationIntent CreateItemIntent()
    {
        return RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 0);
    }

    internal static RealtimeCorpseInteractionIntent CreateCorpseIntent()
    {
        return RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), Guid.NewGuid());
    }

    internal static RealtimeWorldInteractionIntent CreateWorldIntent(Guid simulationSessionId)
    {
        return RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            simulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            1,
            Guid.NewGuid(),
            1);
    }
}
