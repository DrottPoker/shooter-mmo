using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeProtocolTests
{
    [Fact]
    public void JoinRequestRoundTripsThroughVersionedPacket()
    {
        var packet = RealtimeProtocol.EncodeJoinRequest("join-ticket-value");

        var decoded = RealtimeProtocol.TryDecodeJoinRequest(
            packet,
            out var joinTicket,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal("join-ticket-value", joinTicket);
        Assert.True(RealtimeProtocol.TryReadMessageType(packet, out var messageType));
        Assert.Equal(RealtimeMessageType.JoinRequest, messageType);
    }

    [Fact]
    public void JoinAcceptedRoundTripsAllSessionFields()
    {
        var expected = new RealtimeJoinAccepted(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "Protocol Hero",
            "local-world-1",
            GameSimulationCompatibility.Revision,
            "collision-revision-123",
            DateTime.UtcNow.ToString("O"),
            DateTime.UtcNow.AddSeconds(30).ToString("O"),
            true,
            CreateMovementSettings(),
            CreatePlayerState(1f));

        var decoded = RealtimeProtocol.TryDecodeJoinAccepted(
            RealtimeProtocol.EncodeJoinAccepted(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(expected.WorldSessionId, actual.WorldSessionId);
        Assert.Equal(expected.CharacterId, actual.CharacterId);
        Assert.Equal(expected.CharacterName, actual.CharacterName);
        Assert.Equal(expected.WorldId, actual.WorldId);
        Assert.Equal(expected.SimulationRevision, actual.SimulationRevision);
        Assert.Equal(expected.CollisionRevision, actual.CollisionRevision);
        Assert.True(actual.IsReconnect);
        Assert.Equal(30, actual.MovementSettings.TickRateHz);
        Assert.Equal(55f, actual.MovementSettings.MaximumFallSpeed);
        Assert.Equal(0.35f, actual.MovementSettings.CharacterRadius);
        Assert.Equal(6, actual.MovementSettings.MaximumPenetrationIterations);
        Assert.Equal(1f, actual.InitialPlayerState.PositionX);
    }

    [Fact]
    public void DecoderRejectsUnsupportedProtocolVersion()
    {
        var packet = RealtimeProtocol.EncodeJoinRequest("join-ticket-value");
        packet[4] = (byte)(RealtimeProtocol.Version + 1);

        var decoded = RealtimeProtocol.TryDecodeJoinRequest(
            packet,
            out _,
            out var error);

        Assert.False(decoded);
        Assert.Equal("Protocol version is unsupported.", error);
    }

    [Fact]
    public void DecoderRejectsTrailingPacketData()
    {
        var packet = RealtimeProtocol.EncodeLeaveAccepted();
        var malformed = packet.Append((byte)1).ToArray();

        var decoded = RealtimeProtocol.TryDecodeLeaveAccepted(malformed, out var error);

        Assert.False(decoded);
        Assert.Equal("Packet contains trailing data.", error);
    }

    [Fact]
    public void MovementInputBatchRoundTripsSequencedCommands()
    {
        var expected = new[]
        {
            new RealtimeMovementInput(
                10,
                20,
                0.5f,
                0.5f,
                180f,
                RealtimeMovementButtons.Sprint | RealtimeMovementButtons.Aim),
            new RealtimeMovementInput(
                11,
                21,
                0f,
                1f,
                181f,
                RealtimeMovementButtons.Jump)
        };

        var decoded = RealtimeProtocol.TryDecodeMovementInputBatch(
            RealtimeProtocol.EncodeMovementInputBatch(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(2, actual.Length);
        Assert.Equal(11u, actual[1].InputSequence);
        Assert.Equal(RealtimeMovementButtons.Jump, actual[1].Buttons);
    }

    [Fact]
    public void WorldSnapshotRoundTripsChunkMetadataAndPlayers()
    {
        var characterId = Guid.NewGuid().ToString("D");
        var expected = new RealtimeWorldSnapshot(
            4,
            90,
            1,
            3,
            new[]
            {
                new RealtimePlayerSnapshot(characterId, 12, CreatePlayerState(3f))
            });

        var decoded = RealtimeProtocol.TryDecodeWorldSnapshot(
            RealtimeProtocol.EncodeWorldSnapshot(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(4u, actual.SnapshotSequence);
        Assert.Equal(90u, actual.ServerTick);
        Assert.Equal((ushort)1, actual.ChunkIndex);
        Assert.Equal((ushort)3, actual.ChunkCount);
        Assert.Equal(characterId, actual.Players[0].CharacterId);
        Assert.Equal(3f, actual.Players[0].State.PositionX);
    }

    [Fact]
    public void MovementDecoderRejectsNonFiniteInput()
    {
        var packet = RealtimeProtocol.EncodeMovementInputBatch(new[]
        {
            new RealtimeMovementInput(
                1,
                1,
                0f,
                1f,
                0f,
                RealtimeMovementButtons.None)
        });
        var cameraYawOffset = 7 + 1 + 4 + 4 + 4 + 4;
        var nanBytes = BitConverter.GetBytes(float.NaN);
        Array.Copy(nanBytes, 0, packet, cameraYawOffset, nanBytes.Length);

        var decoded = RealtimeProtocol.TryDecodeMovementInputBatch(packet, out _, out var error);

        Assert.False(decoded);
        Assert.Equal("Packet number is not finite.", error);
    }

    private static RealtimeMovementSettings CreateMovementSettings()
    {
        return new RealtimeMovementSettings(
            30,
            15,
            5f,
            8f,
            720f,
            -24f,
            55f,
            7f,
            -2f,
            0f,
            -14f,
            14f,
            -14f,
            14f,
            0.35f,
            2f,
            0.35f,
            45f,
            0.4f,
            0.1f,
            6);
    }

    private static RealtimePlayerState CreatePlayerState(float positionX)
    {
        return new RealtimePlayerState(
            positionX,
            0f,
            -1f,
            0f,
            -2f,
            0f,
            0f,
            true,
            false);
    }
}
