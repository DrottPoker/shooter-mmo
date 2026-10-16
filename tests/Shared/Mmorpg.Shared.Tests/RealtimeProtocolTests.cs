using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;

namespace Mmorpg.Shared.Tests;

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
            "local-shard-1",
            "development-world-1",
            42,
            GameSimulationCompatibility.Revision,
            "collision-revision-123",
            DateTime.UtcNow.ToString("O"),
            DateTime.UtcNow.AddSeconds(30).ToString("O"),
            true,
            new RealtimeCarryState(12, 210, 200),
            CreateMovementSettings(),
            CreatePlayerState(1f));

        var decoded = RealtimeProtocol.TryDecodeJoinAccepted(
            RealtimeProtocol.EncodeJoinAccepted(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(expected.SimulationSessionId, actual.SimulationSessionId);
        Assert.Equal(expected.CharacterId, actual.CharacterId);
        Assert.Equal(expected.CharacterName, actual.CharacterName);
        Assert.Equal(expected.ShardId, actual.ShardId);
        Assert.Equal(expected.WorldId, actual.WorldId);
        Assert.Equal(42ul, actual.ControlledEntityId);
        Assert.Equal(expected.SimulationRevision, actual.SimulationRevision);
        Assert.Equal(expected.CollisionRevision, actual.CollisionRevision);
        Assert.True(actual.IsReconnect);
        Assert.Equal(12, actual.CarryState.ItemStateRevision);
        Assert.Equal(210, actual.CarryState.CarriedWeight);
        Assert.Equal(200, actual.CarryState.CarryCapacity);
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
    public void CarryStateChangeRoundTripsMonotonicItemStateFields()
    {
        var expected = new RealtimeCarryState(42, 280, 200);

        var decoded = RealtimeProtocol.TryDecodeCarryStateChanged(
            RealtimeProtocol.EncodeCarryStateChanged(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(42, actual.ItemStateRevision);
        Assert.Equal(280, actual.CarriedWeight);
        Assert.Equal(200, actual.CarryCapacity);

        var overflow = new RealtimeCarryState(43, 348, 200);
        var overflowDecoded = RealtimeProtocol.TryDecodeCarryStateChanged(
            RealtimeProtocol.EncodeCarryStateChanged(overflow),
            out var overflowActual,
            out var overflowError);

        Assert.True(overflowDecoded, overflowError);
        Assert.Equal(348, overflowActual.CarriedWeight);
        Assert.Throws<ArgumentException>(() =>
            RealtimeProtocol.EncodeCarryStateChanged(
                new RealtimeCarryState(44, -1, 200)));
    }

    [Fact]
    public void ItemOperationIntentsRoundTripEveryPhaseEightMutationKind()
    {
        var operationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var targetItemId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var intents = new[]
        {
            RealtimeItemOperationIntent.CreateRelocate(
                operationId,
                10,
                itemId,
                20,
                destinationId,
                3),
            RealtimeItemOperationIntent.CreateEquip(
                operationId,
                10,
                itemId,
                20,
                "primary_weapon"),
            RealtimeItemOperationIntent.CreateUnequip(
                operationId,
                10,
                itemId,
                20,
                destinationId),
            RealtimeItemOperationIntent.CreateSplitStack(
                operationId,
                10,
                itemId,
                20,
                5,
                destinationId,
                4),
            RealtimeItemOperationIntent.CreateMergeStacks(
                operationId,
                10,
                itemId,
                20,
                targetItemId,
                21),
            RealtimeItemOperationIntent.CreateSwapContainerItems(
                operationId,
                10,
                itemId,
                20,
                targetItemId,
                21),
            RealtimeItemOperationIntent.CreateDestroy(
                operationId,
                10,
                itemId,
                20),
            RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
                operationId,
                10,
                deliveryId,
                30,
                destinationId,
                [new RealtimeItemRevisionExpectation(itemId, 20)])
        };

        foreach (var expected in intents)
        {
            var decoded = RealtimeProtocol.TryDecodeItemOperationIntent(
                RealtimeProtocol.EncodeItemOperationIntent(expected),
                out var actual,
                out var error);

            Assert.True(decoded, error);
            Assert.Equal(expected.OperationId, actual.OperationId);
            Assert.Equal(expected.OperationKind, actual.OperationKind);
            Assert.Equal(expected.ExpectedCharacterRevision, actual.ExpectedCharacterRevision);
            Assert.Equal(expected.ItemInstanceId, actual.ItemInstanceId);
            Assert.Equal(expected.ExpectedItemRevision, actual.ExpectedItemRevision);
            Assert.Equal(expected.TargetItemInstanceId, actual.TargetItemInstanceId);
            Assert.Equal(expected.ExpectedTargetItemRevision, actual.ExpectedTargetItemRevision);
            Assert.Equal(expected.DestinationContainerId, actual.DestinationContainerId);
            Assert.Equal(expected.DestinationSlotIndex, actual.DestinationSlotIndex);
            Assert.Equal(expected.EquipmentSlotId, actual.EquipmentSlotId);
            Assert.Equal(expected.RecoveryDeliveryId, actual.RecoveryDeliveryId);
            Assert.Equal(expected.Items.Length, actual.Items.Length);
        }
    }

    [Fact]
    public void ItemOperationResultRoundTripsCommittedRevisionsAndRefreshSignal()
    {
        var expected = new RealtimeItemOperationResult(
            Guid.NewGuid(),
            RealtimeItemOperationKind.Relocate,
            true,
            true,
            new RealtimeCarryState(42, 210, 250),
            null!,
            [new RealtimeItemRevision(Guid.NewGuid(), 7)],
            [new RealtimeContainerRevision(Guid.NewGuid(), 8)],
            [Guid.NewGuid()]);

        var decoded = RealtimeProtocol.TryDecodeItemOperationResult(
            RealtimeProtocol.EncodeItemOperationResult(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.True(actual.Succeeded);
        Assert.True(actual.RequiresInventoryRefresh);
        Assert.Null(actual.Error);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(42, actual.CarryState.ItemStateRevision);
        Assert.Equal(expected.ItemRevisions[0].ItemInstanceId, actual.ItemRevisions[0].ItemInstanceId);
        Assert.Equal(expected.ContainerRevisions[0].ContainerId, actual.ContainerRevisions[0].ContainerId);
        Assert.Equal(expected.RecoveryDeliveryIds[0], actual.RecoveryDeliveryIds[0]);

        var rejected = new RealtimeItemOperationResult(
            Guid.NewGuid(),
            RealtimeItemOperationKind.Equip,
            false,
            true,
            new RealtimeCarryState(42, 210, 250),
            new RealtimeError("item_state_conflict", "Refresh required."),
            [],
            [],
            []);
        Assert.True(RealtimeProtocol.TryDecodeItemOperationResult(
            RealtimeProtocol.EncodeItemOperationResult(rejected),
            out var rejectedActual,
            out error), error);
        Assert.False(rejectedActual.Succeeded);
        Assert.Equal("item_state_conflict", rejectedActual.Error.Code);
    }

    [Fact]
    public void ItemOperationIntentRejectsUnboundedRecoveryClaims()
    {
        var items = Enumerable.Range(0, 25)
            .Select(_ => new RealtimeItemRevisionExpectation(Guid.NewGuid(), 1))
            .ToArray();
        var intent = RealtimeItemOperationIntent.CreateClaimRecoveryDelivery(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            items);

        Assert.Throws<ArgumentException>(() =>
            RealtimeProtocol.EncodeItemOperationIntent(intent));
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
    public void MovementInputBatchRoundTripsSequencedCommandsAndTheNewestPath()
    {
        var expected = new RealtimeMovementInputBatch(
            [
                new RealtimeMovementInput(10, 20, 3),
                new RealtimeMovementInput(11, 21, 4)
            ],
            new RealtimeMovementPath(
                4,
                [
                    new RealtimeMovementPathWaypoint(1f, 0f, 2f),
                    new RealtimeMovementPathWaypoint(-3.5f, 0.25f, 7f)
                ]));

        var decoded = RealtimeProtocol.TryDecodeMovementInputBatch(
            RealtimeProtocol.EncodeMovementInputBatch(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(2, actual.Inputs.Length);
        Assert.Equal(11u, actual.Inputs[1].InputSequence);
        Assert.Equal(4u, actual.Inputs[1].PathId);
        Assert.NotNull(actual.Path);
        Assert.Equal(4u, actual.Path.PathId);
        Assert.Equal(-3.5f, actual.Path.Waypoints[1].X);
        Assert.Equal(0.25f, actual.Path.Waypoints[1].Y);
    }

    [Fact]
    public void MovementInputBatchRejectsAPathThatDoesNotBelongToTheNewestInput()
    {
        var mismatched = new RealtimeMovementInputBatch(
            [new RealtimeMovementInput(10, 20, 3), new RealtimeMovementInput(11, 21, 4)],
            new RealtimeMovementPath(3, [new RealtimeMovementPathWaypoint(1f, 0f, 2f)]));

        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeMovementInputBatch(mismatched));
        Assert.Equal(
            MovementPath.MaximumWaypoints,
            RealtimeProtocolLimits.MaximumMovementPathWaypoints);
    }

    [Fact]
    public void EntitySpawnAndDespawnRoundTripIdentityAndPresentationMetadata()
    {
        var persistentId = Guid.NewGuid().ToString("D");
        var expectedSpawn = new RealtimeEntitySpawn(
            73,
            RealtimeEntityKind.Player,
            persistentId,
            "Protocol Hero",
            "player.default",
            90,
            CreatePlayerState(3f));

        Assert.True(RealtimeProtocol.TryDecodeEntitySpawn(
            RealtimeProtocol.EncodeEntitySpawn(expectedSpawn),
            out var spawn,
            out var spawnError), spawnError);
        Assert.Equal(73ul, spawn.EntityId);
        Assert.Equal(RealtimeEntityKind.Player, spawn.Kind);
        Assert.Equal(persistentId, spawn.PersistentId);
        Assert.Equal("Protocol Hero", spawn.DisplayName);
        Assert.Equal("player.default", spawn.ArchetypeId);
        Assert.Equal(90u, spawn.ServerTick);

        var expectedDespawn = new RealtimeEntityDespawn(73, "left_shard");
        Assert.True(RealtimeProtocol.TryDecodeEntityDespawn(
            RealtimeProtocol.EncodeEntityDespawn(expectedDespawn),
            out var despawn,
            out var despawnError), despawnError);
        Assert.Equal(73ul, despawn.EntityId);
        Assert.Equal("left_shard", despawn.Reason);
    }

    [Fact]
    public void SimulationSnapshotRoundTripsChunkMetadataAndEntities()
    {
        var expected = new RealtimeSimulationSnapshot(
            4,
            90,
            1,
            3,
            new[]
            {
                new RealtimeEntitySnapshot(73, 12, CreatePlayerState(3f))
            });

        var decoded = RealtimeProtocol.TryDecodeSimulationSnapshot(
            RealtimeProtocol.EncodeSimulationSnapshot(expected),
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(4u, actual.SnapshotSequence);
        Assert.Equal(90u, actual.ServerTick);
        Assert.Equal((ushort)1, actual.ChunkIndex);
        Assert.Equal((ushort)3, actual.ChunkCount);
        Assert.Equal(73ul, actual.Entities[0].EntityId);
        Assert.Equal(3f, actual.Entities[0].State.PositionX);
    }

    [Fact]
    public void OwnerSimulationSnapshotRoundTripsPrioritizedReconciliationState()
    {
        var expected = new RealtimeOwnerSimulationSnapshot(
            5,
            91,
            13,
            CreatePlayerState(4f),
            new RealtimeMovementPathProgress(9, 2, 3));
        var packet = RealtimeProtocol.EncodeOwnerSimulationSnapshot(expected);

        var decoded = RealtimeProtocol.TryDecodeOwnerSimulationSnapshot(
            packet,
            out var actual,
            out var error);

        Assert.True(decoded, error);
        Assert.Equal(RealtimeProtocol.OwnerSimulationSnapshotPacketSize, packet.Length);
        Assert.Equal(5u, actual.SnapshotSequence);
        Assert.Equal(91u, actual.ServerTick);
        Assert.Equal(13u, actual.LastProcessedInputSequence);
        Assert.Equal(4f, actual.State.PositionX);
        Assert.Equal(9u, actual.PathProgress.PathId);
        Assert.Equal(2, actual.PathProgress.NextWaypointIndex);
        Assert.Equal(3, actual.PathProgress.BlockedTicks);
    }

    [Fact]
    public void MaximumSimulationSnapshotChunkStaysInsideProtocolPacketLimit()
    {
        var entities = Enumerable.Range(1, RealtimeProtocol.MaximumSnapshotEntitiesPerChunk)
            .Select(index => new RealtimeEntitySnapshot(
                (ulong)index,
                (uint)index,
                CreatePlayerState(index)))
            .ToArray();
        var packet = RealtimeProtocol.EncodeSimulationSnapshot(
            new RealtimeSimulationSnapshot(1, 1, 0, 1, entities));

        Assert.Equal(
            RealtimeProtocol.SimulationSnapshotPacketHeaderSize
                + (entities.Length * RealtimeProtocol.SimulationSnapshotEntitySize),
            packet.Length);
        Assert.True(packet.Length <= RealtimeProtocol.MaximumUnreliablePacketSize);
    }

    [Fact]
    public void EntityLifecycleRejectsZeroNetworkIds()
    {
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySpawn(
            new RealtimeEntitySpawn(
                0,
                RealtimeEntityKind.Player,
                Guid.NewGuid().ToString("D"),
                "Invalid Hero",
                "player.default",
                1,
                CreatePlayerState(0f))));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntityDespawn(
            new RealtimeEntityDespawn(0, "left_shard")));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshot(
            new RealtimeSimulationSnapshot(
                1,
                1,
                0,
                1,
                [new RealtimeEntitySnapshot(0, 0, CreatePlayerState(0f))])));
    }

    [Fact]
    public void MovementDecoderRejectsNonFiniteWaypoints()
    {
        var packet = RealtimeProtocol.EncodeMovementInputBatch(new RealtimeMovementInputBatch(
            [new RealtimeMovementInput(1, 1, 1)],
            new RealtimeMovementPath(1, [new RealtimeMovementPathWaypoint(0f, 1f, 0f)])));
        // Header, input count, one input, path presence, path id, waypoint count, then x.
        var waypointYOffset = 7 + 1 + 12 + 1 + 4 + 1 + 4;
        var nanBytes = BitConverter.GetBytes(float.NaN);
        Array.Copy(nanBytes, 0, packet, waypointYOffset, nanBytes.Length);

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
            720f,
            -24f,
            55f,
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
            true);
    }
}
