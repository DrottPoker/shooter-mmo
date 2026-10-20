using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.WorldActors;

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
            actor.StateRevision,
            actor.InteractionRevision,
            actor.IsActive,
            actor.PositionX,
            actor.PositionY,
            actor.PositionZ,
            actor.YawDegrees,
            ToActivityTier(actor.ActivityTier),
            bounds.CenterX,
            bounds.CenterY,
            bounds.CenterZ,
            bounds.SizeX,
            bounds.SizeY,
            bounds.SizeZ,
            actor.Definition.ExamineText,
            ToMenuOptions(actor.Definition.Capabilities));
    }

    // The static capability list in authored order, so the client can offer a menu before
    // the interaction opens. Availability is still checked when the interaction opens.
    private static RealtimeWorldActorMenuOption[] ToMenuOptions(
        IReadOnlyList<WorldActorCapabilityDefinition> capabilities)
    {
        var options = new RealtimeWorldActorMenuOption[capabilities.Count];
        for (var index = 0; index < options.Length; index++)
        {
            var capability = capabilities[index];
            options[index] = new RealtimeWorldActorMenuOption(
                capability.Id,
                WorldActorCapabilityRegistry.ToProtocolKind(capability.Kind),
                capability.MenuOption);
        }

        return options;
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
            capability.ProfileId,
            true,
            checked(actor.InteractionRevision + index));
    }

    private static RealtimeWorldActorKind ToKind(string kind)
    {
        return kind switch
        {
            WorldActorKindIds.Npc => RealtimeWorldActorKind.Npc,
            WorldActorKindIds.Mob => RealtimeWorldActorKind.Mob,
            WorldActorKindIds.ResourceNode => RealtimeWorldActorKind.ResourceNode,
            _ => throw new InvalidOperationException(
                "Compiled world actor kind is unsupported: " + kind)
        };
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
