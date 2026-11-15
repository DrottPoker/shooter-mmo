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
            CreatePlayerState(1f),
            new RealtimeShardRules(true));

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
                "main_hand"),
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
        var packet = RealtimeProtocol.EncodeLeaveAccepted(20);
        var malformed = packet.Append((byte)1).ToArray();

        var decoded = RealtimeProtocol.TryDecodeLeaveAccepted(malformed, out _, out var error);

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
            CreatePlayerState(3f),
            new RealtimeCharacterAppearance(
                "Human_Female",
                [new RealtimeAppearanceChoice("face", null, "Freckles"), new RealtimeAppearanceChoice("hair", "", "DarkBrownHair")],
                [new RealtimeVisibleEquipment("head", "clothing.wool_hood"), new RealtimeVisibleEquipment("body", "clothing.blue_wool_robe")],
                true,
                "tool.starter_pickaxe"));

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
        Assert.Equal("Human_Female", spawn.Appearance.BodyModel);
        Assert.Equal(["face=Freckles", "hair=DarkBrownHair"], spawn.Appearance.Choices.Select(choice => $"{choice.Kind}{choice.Slot}={choice.Option}"));
        Assert.Equal(["head", "body"], spawn.Appearance.Equipment.Select(item => item.EquipmentSlotId));
        Assert.True(spawn.Appearance.HeadwearLowered);
        Assert.Equal("tool.starter_pickaxe", spawn.Appearance.ToolInUse);

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
                RealtimeEntitySnapshot.Quantize(73, 3f, -1.5f, -1f, 90f, true),
                new RealtimeEntitySnapshot(uint.MaxValue, -(1 << 23), short.MinValue, (1 << 23) - 1, 1023, false)
            });

        var packet = RealtimeProtocol.EncodeSimulationSnapshot(expected);
        var decoded = RealtimeProtocol.TryDecodeSimulationSnapshot(packet, out var actual, out var error);

        Assert.True(decoded, error);
        Assert.Equal(RealtimeProtocol.SimulationSnapshotPacketHeaderSize + (2 * 14), packet.Length);
        Assert.Equal(4u, actual.SnapshotSequence);
        Assert.Equal(90u, actual.ServerTick);
        Assert.Equal((ushort)1, actual.ChunkIndex);
        Assert.Equal((ushort)3, actual.ChunkCount);
        var near = actual.Entities[0];
        Assert.Equal(
            (73u, 3f, -1.5f, -1f, 90f, true),
            (near.EntityId, near.PositionX, near.PositionY, near.PositionZ, near.YawDegrees, near.IsGrounded));
        var edge = actual.Entities[1];
        Assert.Equal(
            (uint.MaxValue, -65536f, -512f, 65535.9921875f, 359.6484375f, false),
            (edge.EntityId, edge.PositionX, edge.PositionY, edge.PositionZ, edge.YawDegrees, edge.IsGrounded));
    }

    [Fact]
    public void QuantizationRoundsToTheNearestStepAndClampsToTheRange()
    {
        Assert.True(RealtimeEntitySnapshot.TryQuantizeHorizontal(1.004f, out var nearStep));
        Assert.Equal(129, nearStep);
        Assert.True(RealtimeEntitySnapshot.TryQuantizeHorizontal(-0.01171875f, out var negativeHalfStep));
        Assert.Equal(-2, negativeHalfStep);
        Assert.True(RealtimeEntitySnapshot.TryQuantizeHorizontal(70_000f, out var high));
        Assert.Equal((1 << 23) - 1, high);
        Assert.True(RealtimeEntitySnapshot.TryQuantizeHorizontal(-70_000f, out var low));
        Assert.Equal(-(1 << 23), low);
        Assert.True(RealtimeEntitySnapshot.TryQuantizeVertical(-600f, out var deep));
        Assert.Equal(short.MinValue, deep);
        Assert.True(RealtimeEntitySnapshot.TryQuantizeVertical(1.5078125f, out var height));
        Assert.Equal(97, height);
        Assert.False(RealtimeEntitySnapshot.TryQuantizeHorizontal(float.NaN, out _));
        Assert.False(RealtimeEntitySnapshot.TryQuantizeVertical(float.PositiveInfinity, out _));
        Assert.False(RealtimeEntitySnapshot.TryQuantizeYaw(float.NegativeInfinity, out _));

        // Any turn, to the nearest of 1024 steps, and a full turn wraps to 0.
        Assert.Equal(0, Yaw(0f));
        Assert.Equal(256, Yaw(90f));
        Assert.Equal(768, Yaw(-90f));
        Assert.Equal(256, Yaw(450f));
        Assert.Equal(0, Yaw(359.9f));
        Assert.Equal(1023, Yaw(359.6f));
        Assert.Throws<ArgumentException>(() => RealtimeEntitySnapshot.Quantize(1, float.NaN, 0f, 0f, 0f, true));
    }

    [Fact]
    public void SnapshotEncodersIntoACallersBufferWriteTheBytesOfTheAllocatingEncoder()
    {
        var entities = new[]
        {
            new RealtimeEntitySnapshot(7, 1000, -64, -1000, 512, true),
            new RealtimeEntitySnapshot(8, -(1 << 23), 32767, (1 << 23) - 1, 1023, false)
        };
        var expected = RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(9, 10, 1, 2, entities));
        var buffer = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];

        var length = RealtimeProtocol.EncodeSimulationSnapshotHeader(buffer, 9, 10, 1, 2, entities.Length);
        Assert.Equal(RealtimeProtocol.SimulationSnapshotPacketHeaderSize, length);
        foreach (var entity in entities)
        {
            RealtimeProtocol.EncodeEntitySnapshot(
                buffer.AsSpan(length),
                entity.EntityId,
                entity.QuantizedX,
                entity.QuantizedY,
                entity.QuantizedZ,
                entity.YawStep,
                entity.IsGrounded);
            length += RealtimeProtocol.SimulationSnapshotEntitySize;
        }

        Assert.Equal(expected, buffer.AsSpan(0, length).ToArray());
        Assert.True(RealtimeProtocol.TryReadMessageType(buffer.AsSpan(0, length), out var type));
        Assert.Equal(RealtimeMessageType.SimulationSnapshot, type);
    }

    [Fact]
    public void SnapshotEncodersIntoACallersBufferRejectInvalidValuesAndShortBuffers()
    {
        var buffer = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];

        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySnapshot(buffer, 0, 0, 0, 0, 0, true));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySnapshot(buffer, 1, 1 << 23, 0, 0, 0, true));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySnapshot(buffer, 1, 0, 0, -(1 << 23) - 1, 0, true));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySnapshot(buffer, 1, 0, 0, 0, 1024, true));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntitySnapshot(new byte[13], 1, 0, 0, 0, 0, true));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshotHeader(buffer, 1, 1, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshotHeader(buffer, 1, 1, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshotHeader(buffer, 1, 1, 0, 1, 0));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshotHeader(
            buffer,
            1,
            1,
            0,
            1,
            RealtimeProtocol.MaximumSnapshotEntitiesPerChunk + 1));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshotHeader(new byte[20 + 13], 1, 1, 0, 1, 1));
    }

    [Fact]
    public void TheOwnerSnapshotEncoderIntoACallersBufferWritesTheBytesOfTheAllocatingEncoder()
    {
        var expected = RealtimeProtocol.EncodeOwnerSimulationSnapshot(new RealtimeOwnerSimulationSnapshot(
            41,
            900,
            77,
            new RealtimePlayerState(1.5f, -2.25f, 300.125f, 4.5f, -0.5f, 0f, 271.5f, true),
            new RealtimeMovementPathProgress(12, 3, 2)));
        var buffer = new byte[RealtimeProtocol.MaximumUnreliablePacketSize];

        var length = RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            buffer, 41, 900, 77, 1.5f, -2.25f, 300.125f, 4.5f, -0.5f, 0f, 271.5f, true, 12, 3, 2);

        Assert.Equal(RealtimeProtocol.OwnerSimulationSnapshotPacketSize, length);
        Assert.Equal(expected, buffer.AsSpan(0, length).ToArray());

        // The same rules as the message: finite values, a waypoint within the path limit, and no
        // progress without a path.
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            buffer, 1, 1, 1, float.NaN, 0f, 0f, 0f, 0f, 0f, 0f, true, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            buffer, 1, 1, 1, 0f, 0f, 0f, 0f, 0f, 0f, 0f, true, 5, RealtimeProtocolLimits.MaximumMovementPathWaypoints + 1, 0));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            buffer, 1, 1, 1, 0f, 0f, 0f, 0f, 0f, 0f, 0f, true, 0, 1, 0));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            new byte[RealtimeProtocol.OwnerSimulationSnapshotPacketSize - 1], 1, 1, 1, 0f, 0f, 0f, 0f, 0f, 0f, 0f, true, 0, 0, 0));
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
            .Select(index => RealtimeEntitySnapshot.Quantize((uint)index, index, 0f, -1f, 0f, true))
            .ToArray();
        var packet = RealtimeProtocol.EncodeSimulationSnapshot(
            new RealtimeSimulationSnapshot(1, 1, 0, 1, entities));

        Assert.Equal(14, RealtimeProtocol.SimulationSnapshotEntitySize);
        Assert.Equal(71, RealtimeProtocol.MaximumSnapshotEntitiesPerChunk);
        Assert.Equal(
            RealtimeProtocol.SimulationSnapshotPacketHeaderSize
                + (entities.Length * RealtimeProtocol.SimulationSnapshotEntitySize),
            packet.Length);
        Assert.True(packet.Length <= RealtimeProtocol.MaximumUnreliablePacketSize);

        // One record more would not fit.
        Assert.True(packet.Length + RealtimeProtocol.SimulationSnapshotEntitySize > RealtimeProtocol.MaximumUnreliablePacketSize);
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
                CreatePlayerState(0f),
                new RealtimeCharacterAppearance("Human_Male", null, null, false, null))));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeEntityDespawn(
            new RealtimeEntityDespawn(0, "left_shard")));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeSimulationSnapshot(
            new RealtimeSimulationSnapshot(
                1,
                1,
                0,
                1,
                [new RealtimeEntitySnapshot(0, 0, 0, 0, 0, true)])));
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

    private static int Yaw(float degrees)
    {
        Assert.True(RealtimeEntitySnapshot.TryQuantizeYaw(degrees, out var step));
        return step;
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
