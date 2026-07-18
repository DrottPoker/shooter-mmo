using ShooterMmo.GameProtocol;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RealtimeWorldActorProtocolTests
{
    [Fact]
    public void ActorPresenceAndStateRoundTripWithoutCapabilities()
    {
        var runtimeActorId = Guid.NewGuid();
        var spawn = CreateSpawn(runtimeActorId);
        var packet = RealtimeProtocol.EncodeWorldActorSpawn(spawn);

        Assert.True(
            RealtimeProtocol.TryDecodeWorldActorSpawn(packet, out var decoded, out var error),
            error);
        Assert.Equal(spawn.EntityId, decoded.EntityId);
        Assert.Equal(runtimeActorId, decoded.RuntimeActorId);
        Assert.Equal("npc.city_services", decoded.ActorDefinitionId);
        Assert.Equal("local.city_services", decoded.SpawnDefinitionId);
        Assert.Equal(RealtimeWorldActorKind.Npc, decoded.Kind);
        Assert.Equal(RealtimeWorldActorActivityTier.EventDriven, decoded.ActivityTier);
        Assert.DoesNotContain(
            "services.vendor",
            System.Text.Encoding.UTF8.GetString(packet),
            StringComparison.Ordinal);
        Assert.True(packet.Length <= RealtimeProtocol.MaximumPacketSize);

        var state = new RealtimeWorldActorState(
            88,
            2,
            3,
            true,
            2.5f,
            0f,
            -1f,
            270f,
            RealtimeWorldActorActivityTier.EventDriven);
        Assert.True(
            RealtimeProtocol.TryDecodeWorldActorState(
                RealtimeProtocol.EncodeWorldActorState(state),
                out var decodedState,
                out error),
            error);
        Assert.Equal(2, decodedState.StateRevision);
        Assert.Equal(3, decodedState.InteractionRevision);

        var despawn = new RealtimeWorldActorDespawn(88, runtimeActorId, "assignment_changed");
        Assert.True(
            RealtimeProtocol.TryDecodeWorldActorDespawn(
                RealtimeProtocol.EncodeWorldActorDespawn(despawn),
                out var decodedDespawn,
                out error),
            error);
        Assert.Equal("assignment_changed", decodedDespawn.Reason);
    }

    [Fact]
    public void InteractionMessagesRoundTripWithOperationCorrelation()
    {
        var operationId = Guid.NewGuid();
        var simulationSessionId = Guid.NewGuid();
        var interactionSessionId = Guid.NewGuid();
        var runtimeActorId = Guid.NewGuid();
        var open = RealtimeWorldInteractionIntent.CreateOpen(
            operationId,
            simulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            88,
            runtimeActorId,
            3);

        Assert.True(
            RealtimeProtocol.TryDecodeWorldInteractionIntent(
                RealtimeProtocol.EncodeWorldInteractionIntent(open),
                out var decodedOpen,
                out var error),
            error);
        Assert.Equal(operationId, decodedOpen.OperationId);
        Assert.Equal(simulationSessionId, decodedOpen.SimulationSessionId);
        Assert.Equal(RealtimeWorldInteractionOperationKind.Open, decodedOpen.OperationKind);

        var capabilities = new[]
        {
            new RealtimeWorldActorCapability(
                "services.dialogue",
                RealtimeWorldActorCapabilityKind.Dialogue,
                "Talk",
                true,
                1),
            new RealtimeWorldActorCapability(
                "services.vendor",
                RealtimeWorldActorCapabilityKind.Vendor,
                "Trade",
                false,
                4)
        };
        var opened = new RealtimeWorldInteractionOpened(
            operationId,
            interactionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            88,
            runtimeActorId,
            3,
            9,
            "Mira the Quartermaster",
            capabilities);
        var openedPacket = RealtimeProtocol.EncodeWorldInteractionOpened(opened);

        Assert.True(
            RealtimeProtocol.TryDecodeWorldInteractionOpened(
                openedPacket,
                out var decodedOpened,
                out error),
            error);
        Assert.Equal(2, decodedOpened.Capabilities.Length);
        Assert.True(decodedOpened.Capabilities[0].IsAvailable);
        Assert.False(decodedOpened.Capabilities[1].IsAvailable);
        Assert.True(openedPacket.Length <= RealtimeProtocol.MaximumPacketSize);

        var action = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            simulationSessionId,
            interactionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            88,
            runtimeActorId,
            3,
            "services.dialogue",
            RealtimeWorldActorCapabilityKind.Dialogue,
            1);
        Assert.True(
            RealtimeProtocol.TryDecodeWorldInteractionIntent(
                RealtimeProtocol.EncodeWorldInteractionIntent(action),
                out var decodedAction,
                out error),
            error);
        Assert.Equal("services.dialogue", decodedAction.CapabilityId);
        Assert.Equal(
            RealtimeWorldActorCapabilityKind.Dialogue,
            decodedAction.CapabilityKind);
        Assert.Equal(1, decodedAction.ExpectedCapabilityRevision);

        var result = new RealtimeWorldInteractionResult(
            action.OperationId,
            interactionSessionId,
            RealtimeWorldInteractionOperationKind.CapabilityAction,
            false,
            3,
            new RealtimeError(
                "world_interaction_capability_deferred",
                "This capability handler is not implemented in Phase 12."));
        Assert.True(
            RealtimeProtocol.TryDecodeWorldInteractionResult(
                RealtimeProtocol.EncodeWorldInteractionResult(result),
                out var decodedResult,
                out error),
            error);
        Assert.Equal("world_interaction_capability_deferred", decodedResult.Error.Code);

        var closed = new RealtimeWorldInteractionClosed(
            interactionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            88,
            runtimeActorId,
            "world_interaction_out_of_range",
            "Move closer to continue interacting.");
        Assert.True(
            RealtimeProtocol.TryDecodeWorldInteractionClosed(
                RealtimeProtocol.EncodeWorldInteractionClosed(closed),
                out var decodedClosed,
                out error),
            error);
        Assert.Equal(interactionSessionId, decodedClosed.InteractionSessionId);
    }

    [Fact]
    public void MalformedAndOversizedActorPacketsAreRejected()
    {
        var packet = RealtimeProtocol.EncodeWorldActorSpawn(CreateSpawn(Guid.NewGuid()));
        Array.Resize(ref packet, packet.Length - 1);

        Assert.False(RealtimeProtocol.TryDecodeWorldActorSpawn(packet, out _, out _));

        var oversized = CreateSpawn(Guid.NewGuid(), new string('x', 129));
        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeWorldActorSpawn(oversized));
    }

    [Fact]
    public void OrdinaryActorPresenceDoesNotExposeCapabilitySummaryState()
    {
        var propertyNames = typeof(RealtimeWorldActorSpawn)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Capabilities", propertyNames);
        Assert.DoesNotContain("CapabilitySummaryRevision", propertyNames);
    }

    [Fact]
    public void InvalidInteractionShapeCannotBeEncoded()
    {
        var intent = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            RealtimeWorldInteractionTargetKind.WorldActor,
            88,
            Guid.NewGuid(),
            1,
            "services.dialogue",
            RealtimeWorldActorCapabilityKind.Dialogue,
            1);

        Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeWorldInteractionIntent(intent));

        var invalidResult = new RealtimeWorldInteractionResult(
            Guid.NewGuid(),
            Guid.Empty,
            RealtimeWorldInteractionOperationKind.CapabilityAction,
            true,
            1,
            null!);
        Assert.Throws<ArgumentException>(
            () => RealtimeProtocol.EncodeWorldInteractionResult(invalidResult));
    }

    private static RealtimeWorldActorSpawn CreateSpawn(
        Guid runtimeActorId,
        string displayName = "Mira the Quartermaster")
    {
        return new RealtimeWorldActorSpawn(
            88,
            runtimeActorId,
            "npc.city_services",
            "local.city_services",
            displayName,
            RealtimeWorldActorKind.Npc,
            "city",
            RealtimeWorldActorDisposition.Friendly,
            "npc.city_services",
            2.5f,
            0f,
            -1f,
            270f,
            1,
            1,
            true,
            0f,
            1f,
            0f,
            0.8f,
            2f,
            0.8f,
            RealtimeWorldActorActivityTier.EventDriven);
    }
}
