using ShooterMmo.GameProtocol;
using ShooterMmo.WorldData.Actors;
using SimulationWorker.Entities;

namespace SimulationWorker.WorldActors;

internal static class WorldActorProtocolMapper
{
    public static RealtimeWorldActorSpawn ToSpawn(WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var bounds = actor.Definition.InteractionBounds;
        return new RealtimeWorldActorSpawn(
            actor.NetworkEntityId,
            actor.RuntimeActorId,
            actor.Definition.Id,
            actor.Spawn.SpawnDefinitionId,
            actor.Definition.DisplayName,
            ToKind(actor.Definition.Kind),
            actor.Definition.FactionId,
            ToDisposition(actor.Definition.Disposition),
            actor.Definition.PresentationArchetypeId,
            actor.PositionX,
            actor.PositionY,
            actor.PositionZ,
            actor.YawDegrees,
            actor.StateRevision,
            actor.InteractionRevision,
            actor.IsActive,
            bounds.CenterX,
            bounds.CenterY,
            bounds.CenterZ,
            bounds.SizeX,
            bounds.SizeY,
            bounds.SizeZ,
            ToActivityTier(actor.ActivityTier));
    }

    public static RealtimeWorldActorState ToState(WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new RealtimeWorldActorState(
            actor.NetworkEntityId,
            actor.StateRevision,
            actor.InteractionRevision,
            actor.IsActive,
            actor.PositionX,
            actor.PositionY,
            actor.PositionZ,
            actor.YawDegrees,
            ToActivityTier(actor.ActivityTier));
    }

    public static RealtimeWorldActorCapability ToCapability(
        WorldActorRuntimeState actor,
        WorldActorCapabilityDefinition capability,
        int index)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(capability);
        return new RealtimeWorldActorCapability(
            capability.Id,
            WorldActorCapabilityRegistry.ToProtocolKind(capability.Kind),
            capability.DisplayName,
            true,
            checked(actor.InteractionRevision + index));
    }

    private static RealtimeWorldActorKind ToKind(string kind)
    {
        return string.Equals(kind, WorldActorKindIds.Npc, StringComparison.Ordinal)
            ? RealtimeWorldActorKind.Npc
            : RealtimeWorldActorKind.Mob;
    }

    private static RealtimeWorldActorDisposition ToDisposition(string disposition)
    {
        return disposition switch
        {
            WorldActorDispositionIds.Friendly => RealtimeWorldActorDisposition.Friendly,
            WorldActorDispositionIds.Neutral => RealtimeWorldActorDisposition.Neutral,
            _ => RealtimeWorldActorDisposition.Hostile
        };
    }

    private static RealtimeWorldActorActivityTier ToActivityTier(string activityTier)
    {
        return activityTier switch
        {
            WorldActorActivityTierIds.EventDriven =>
                RealtimeWorldActorActivityTier.EventDriven,
            WorldActorActivityTierIds.Active => RealtimeWorldActorActivityTier.Active,
            _ => RealtimeWorldActorActivityTier.Dormant
        };
    }
}

internal static class WorldActorProtocolCompatibility
{
    public static void Validate(WorldActorRuntimeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var store = new WorldActorStore(document, new SimulationEntityRegistry());
        Validate(store.ActivateAssignment(
            document.WorldId,
            "protocol-validation-shard",
            "protocol-validation-runtime"));
    }

    public static void Validate(IReadOnlyCollection<WorldActorRuntimeState> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        foreach (var actor in actors)
        {
            try
            {
                _ = RealtimeProtocol.EncodeWorldActorSpawn(
                    WorldActorProtocolMapper.ToSpawn(actor));
                var capabilities = actor.Definition.Capabilities
                    .Select((capability, index) =>
                        WorldActorProtocolMapper.ToCapability(actor, capability, index))
                    .ToArray();
                _ = RealtimeProtocol.EncodeWorldInteractionOpened(
                    new RealtimeWorldInteractionOpened(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        RealtimeWorldInteractionTargetKind.WorldActor,
                        actor.NetworkEntityId,
                        actor.RuntimeActorId,
                        actor.InteractionRevision,
                        1,
                        actor.Definition.DisplayName,
                        capabilities));
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    "World actor '" + actor.Definition.Id
                        + "' cannot fit the realtime protocol contract.",
                    exception);
            }
        }
    }
}
