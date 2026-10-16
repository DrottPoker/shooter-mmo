#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // World actor presence and NPC interaction.
    public static partial class RealtimeProtocolGolden
    {
        private const ulong ActorEntityId = 0x2122232425262728;

        private static void RegisterWorldActors(Registry r)
        {
            const RealtimeMessageType spawn = RealtimeMessageType.WorldActorSpawn;
            const RealtimeMessageType state = RealtimeMessageType.WorldActorState;
            const RealtimeMessageType despawn = RealtimeMessageType.WorldActorDespawn;
            const RealtimeMessageType opened = RealtimeMessageType.WorldInteractionOpened;
            const RealtimeMessageType result = RealtimeMessageType.WorldInteractionResult;
            const RealtimeMessageType closed = RealtimeMessageType.WorldInteractionClosed;

            r.Vector("WorldActors/WorldActorSpawn/npc", spawn, () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel()));
            r.Vector(
                "WorldActors/WorldActorSpawn/mob",
                spawn,
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(
                    kind: RealtimeWorldActorKind.Mob,
                    disposition: RealtimeWorldActorDisposition.Hostile,
                    stateRevision: long.MaxValue,
                    isActive: false,
                    activityTier: RealtimeWorldActorActivityTier.Active)));
            r.Vector(
                "WorldActors/WorldActorState/typical",
                state,
                () => RealtimeProtocol.EncodeWorldActorState(new RealtimeWorldActorState(
                    entityId: ActorEntityId,
                    stateRevision: 3,
                    interactionRevision: 4,
                    isActive: true,
                    positionX: 10.5f,
                    positionY: 0.25f,
                    positionZ: -20.75f,
                    yawDegrees: 90f,
                    activityTier: RealtimeWorldActorActivityTier.EventDriven)));
            r.Vector(
                "WorldActors/WorldActorState/dormant",
                state,
                () => RealtimeProtocol.EncodeWorldActorState(new RealtimeWorldActorState(
                    entityId: 1,
                    stateRevision: long.MaxValue,
                    interactionRevision: 1,
                    isActive: false,
                    positionX: -0f,
                    positionY: float.MaxValue,
                    positionZ: float.Epsilon,
                    yawDegrees: -180f,
                    activityTier: RealtimeWorldActorActivityTier.Dormant)));
            r.Vector(
                "WorldActors/WorldActorDespawn/typical",
                despawn,
                () => RealtimeProtocol.EncodeWorldActorDespawn(new RealtimeWorldActorDespawn(
                    entityId: ActorEntityId,
                    runtimeActorId: G(6),
                    reason: "despawned")));

            RegisterWorldInteractionIntentVectors(r);

            r.Vector(
                "WorldActors/WorldInteractionOpened/no capabilities",
                opened,
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(Array.Empty<RealtimeWorldActorCapability>())));
            r.Vector(
                "WorldActors/WorldInteractionOpened/three capabilities",
                opened,
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(new[]
                {
                    new RealtimeWorldActorCapability("dialogue.greeting", RealtimeWorldActorCapabilityKind.Dialogue, "Prata", true, 2),
                    new RealtimeWorldActorCapability("insurance.basic", RealtimeWorldActorCapabilityKind.Insurance, "Försäkra ✓", false, 3),
                    new RealtimeWorldActorCapability("storage.recovery", RealtimeWorldActorCapabilityKind.RecoveryStorage, "Hämta", true, long.MaxValue)
                })));
            r.Vector(
                "WorldActors/WorldInteractionOpened/sixteen capabilities",
                opened,
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(Enumerable.Range(0, 16)
                    .Select(index => new RealtimeWorldActorCapability(
                        "capability." + index.ToString("00"),
                        (RealtimeWorldActorCapabilityKind)(1 + (index % 9)),
                        "Capability " + index.ToString("00"),
                        index % 2 == 0,
                        1 + index))
                    .ToArray())));
            r.Vector(
                "WorldActors/WorldInteractionResult/close succeeded",
                result,
                () => RealtimeProtocol.EncodeWorldInteractionResult(new RealtimeWorldInteractionResult(
                    operationId: G(7),
                    interactionSessionId: G(9),
                    operationKind: RealtimeWorldInteractionOperationKind.Close,
                    succeeded: true,
                    targetRevision: 4,
                    error: null,
                    message: "Farväl ✓",
                    itemStateRevision: 0)));
            r.Vector(
                "WorldActors/WorldInteractionResult/capability succeeded",
                result,
                () => RealtimeProtocol.EncodeWorldInteractionResult(new RealtimeWorldInteractionResult(
                    operationId: G(7),
                    interactionSessionId: G(9),
                    operationKind: RealtimeWorldInteractionOperationKind.CapabilityAction,
                    succeeded: true,
                    targetRevision: long.MaxValue,
                    error: null,
                    message: "",
                    itemStateRevision: 77)));
            r.Vector(
                "WorldActors/WorldInteractionResult/open failed",
                result,
                () => RealtimeProtocol.EncodeWorldInteractionResult(new RealtimeWorldInteractionResult(
                    operationId: G(7),
                    interactionSessionId: Guid.Empty,
                    operationKind: RealtimeWorldInteractionOperationKind.Open,
                    succeeded: false,
                    targetRevision: 0,
                    error: new RealtimeError("interaction_out_of_range", "Move closer to talk."),
                    message: "",
                    itemStateRevision: 0)));
            r.Vector(
                "WorldActors/WorldInteractionResult/capability failed with message",
                result,
                () => RealtimeProtocol.EncodeWorldInteractionResult(new RealtimeWorldInteractionResult(
                    operationId: G(7),
                    interactionSessionId: G(9),
                    operationKind: RealtimeWorldInteractionOperationKind.CapabilityAction,
                    succeeded: false,
                    targetRevision: 4,
                    error: new RealtimeError("insurance_unavailable", "Insurance is not offered."),
                    message: " ",
                    itemStateRevision: 12)));
            r.Vector(
                "WorldActors/WorldInteractionClosed/typical",
                closed,
                () => RealtimeProtocol.EncodeWorldInteractionClosed(new RealtimeWorldInteractionClosed(
                    interactionSessionId: G(9),
                    targetKind: RealtimeWorldInteractionTargetKind.Corpse,
                    targetEntityId: ActorEntityId,
                    targetRuntimeId: G(6),
                    code: "target_despawned",
                    message: "The target is gone.")));

            RegisterWorldActorPresenceCases(r);
            RegisterWorldInteractionIntentCases(r);
            RegisterWorldInteractionResponseCases(r);
        }

        private static void RegisterWorldInteractionIntentVectors(Registry r)
        {
            const RealtimeMessageType intent = RealtimeMessageType.WorldInteractionIntent;
            const RealtimeWorldInteractionTargetKind actor = RealtimeWorldInteractionTargetKind.WorldActor;

            r.Vector(
                "WorldActors/WorldInteractionIntent/open actor",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(
                    RealtimeWorldInteractionIntent.CreateOpen(G(7), G(8), actor, ActorEntityId, G(6), 4)));
            r.Vector(
                "WorldActors/WorldInteractionIntent/open corpse",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateOpen(
                    G(7),
                    G(8),
                    RealtimeWorldInteractionTargetKind.Corpse,
                    ulong.MaxValue,
                    G(6),
                    long.MaxValue)));
            r.Vector(
                "WorldActors/WorldInteractionIntent/close",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(
                    RealtimeWorldInteractionIntent.CreateClose(G(7), G(8), G(9), actor, ActorEntityId, G(6), 4)));
            r.Vector(
                "WorldActors/WorldInteractionIntent/dialogue",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    actor,
                    ActorEntityId,
                    G(6),
                    4,
                    "dialogue.greeting",
                    RealtimeWorldActorCapabilityKind.Dialogue,
                    2)));
            foreach (var lifecycle in new[]
                {
                    RealtimeNpcLifecycleActionKind.ApplyInsurance,
                    RealtimeNpcLifecycleActionKind.RemoveInsurance
                })
            {
                r.Vector(
                    "WorldActors/WorldInteractionIntent/" + lifecycle,
                    intent,
                    () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                        G(7),
                        G(8),
                        G(9),
                        actor,
                        ActorEntityId,
                        G(6),
                        4,
                        "insurance.basic",
                        RealtimeWorldActorCapabilityKind.Insurance,
                        3,
                        lifecycle,
                        11,
                        G(2),
                        6)));
            }

            r.Vector(
                "WorldActors/WorldInteractionIntent/AcceptQuest",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    actor,
                    ActorEntityId,
                    G(6),
                    4,
                    "quest.first",
                    RealtimeWorldActorCapabilityKind.QuestOffer,
                    5,
                    RealtimeNpcLifecycleActionKind.AcceptQuest,
                    0,
                    Guid.Empty,
                    0,
                    G(3),
                    9)));
            r.Vector(
                "WorldActors/WorldInteractionIntent/AbandonQuest",
                intent,
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    actor,
                    ActorEntityId,
                    G(6),
                    4,
                    "quest.first",
                    RealtimeWorldActorCapabilityKind.QuestOffer,
                    5,
                    RealtimeNpcLifecycleActionKind.AbandonQuest,
                    long.MaxValue)));
        }

        private static void RegisterWorldActorPresenceCases(Registry r)
        {
            r.Accepts("WorldActors/WorldActorSpawn/baseline", WorldActorSpawnWire());
            r.Rejects("WorldActors/WorldActorSpawn/entity zero", WorldActorSpawnWire(entityId: 0));
            r.Rejects("WorldActors/WorldActorSpawn/empty runtime id", WorldActorSpawnWire(runtimeActorId: Guid.Empty));
            r.Rejects("WorldActors/WorldActorSpawn/whitespace actor definition", WorldActorSpawnWire(actorDefinitionId: " "));
            r.Accepts("WorldActors/WorldActorSpawn/128 character spawn definition", WorldActorSpawnWire(spawnDefinitionId: Repeat("s", 128)));
            r.Rejects("WorldActors/WorldActorSpawn/129 character spawn definition", WorldActorSpawnWire(spawnDefinitionId: Repeat("s", 129)));
            r.Rejects("WorldActors/WorldActorSpawn/whitespace display name", WorldActorSpawnWire(displayName: "\t"));
            r.Rejects("WorldActors/WorldActorSpawn/kind 0", WorldActorSpawnWire(kind: 0));
            r.Rejects("WorldActors/WorldActorSpawn/kind 3", WorldActorSpawnWire(kind: 3));
            r.Rejects("WorldActors/WorldActorSpawn/whitespace faction", WorldActorSpawnWire(factionId: " "));
            r.Rejects("WorldActors/WorldActorSpawn/disposition 0", WorldActorSpawnWire(disposition: 0));
            r.Rejects("WorldActors/WorldActorSpawn/disposition 4", WorldActorSpawnWire(disposition: 4));
            r.Rejects("WorldActors/WorldActorSpawn/whitespace presentation", WorldActorSpawnWire(presentationArchetypeId: " "));
            r.Rejects("WorldActors/WorldActorSpawn/state revision 0", WorldActorSpawnWire(payload: ActorStatePayloadWire(stateRevision: 0)));
            r.Rejects(
                "WorldActors/WorldActorSpawn/interaction revision 0",
                WorldActorSpawnWire(payload: ActorStatePayloadWire(interactionRevision: 0)));
            r.Rejects("WorldActors/WorldActorSpawn/activity tier 0", WorldActorSpawnWire(payload: ActorStatePayloadWire(activityTier: 0)));
            r.Rejects("WorldActors/WorldActorSpawn/activity tier 4", WorldActorSpawnWire(payload: ActorStatePayloadWire(activityTier: 4)));
            r.Rejects("WorldActors/WorldActorSpawn/NaN position", WorldActorSpawnWire(payload: ActorStatePayloadWire(positionXBits: QuietNaNBits)));
            r.Rejects("WorldActors/WorldActorSpawn/bounds width 0", WorldActorSpawnWire(boundsSizeX: 0f));
            r.Rejects("WorldActors/WorldActorSpawn/negative bounds height", WorldActorSpawnWire(boundsSizeY: -1f));
            r.Rejects("WorldActors/WorldActorSpawn/NaN bounds depth", WorldActorSpawnWire(boundsSizeZBits: QuietNaNBits));
            r.Rejects("WorldActors/WorldActorSpawn/infinite bounds center", WorldActorSpawnWire(boundsCenterXBits: PositiveInfinityBits));
            r.Accepts("WorldActors/WorldActorSpawn/negative bounds center", WorldActorSpawnWire(boundsCenterX: -500f));
            r.AcceptsAs(
                "WorldActors/WorldActorSpawn/active byte 2 reads as true",
                WorldActorSpawnWire(payload: ActorStatePayloadWire(isActive: 2)),
                WorldActorSpawnWire());

            r.Accepts("WorldActors/WorldActorState/baseline", WorldActorStateWire());
            r.Rejects("WorldActors/WorldActorState/entity zero", WorldActorStateWire(entityId: 0));
            r.Rejects("WorldActors/WorldActorState/state revision 0", WorldActorStateWire(payload: ActorStatePayloadWire(stateRevision: 0)));
            r.Rejects(
                "WorldActors/WorldActorState/negative interaction revision",
                WorldActorStateWire(payload: ActorStatePayloadWire(interactionRevision: -1)));
            r.Rejects("WorldActors/WorldActorState/activity tier 0", WorldActorStateWire(payload: ActorStatePayloadWire(activityTier: 0)));
            r.Accepts("WorldActors/WorldActorState/dormant", WorldActorStateWire(payload: ActorStatePayloadWire(activityTier: 2)));
            r.Rejects(
                "WorldActors/WorldActorState/infinite position",
                WorldActorStateWire(payload: ActorStatePayloadWire(positionXBits: NegativeInfinityBits)));
            r.AcceptsAs(
                "WorldActors/WorldActorState/active byte 9 reads as true",
                WorldActorStateWire(payload: ActorStatePayloadWire(isActive: 9)),
                WorldActorStateWire());

            r.Accepts("WorldActors/WorldActorDespawn/baseline", WorldActorDespawnWire());
            r.Rejects("WorldActors/WorldActorDespawn/entity zero", WorldActorDespawnWire(entityId: 0));
            r.Rejects("WorldActors/WorldActorDespawn/empty runtime id", WorldActorDespawnWire(runtimeActorId: Guid.Empty));
            r.Rejects("WorldActors/WorldActorDespawn/whitespace reason", WorldActorDespawnWire(reason: " "));
            r.Rejects("WorldActors/WorldActorDespawn/empty reason", WorldActorDespawnWire(reason: ""));
            r.Rejects("WorldActors/WorldActorDespawn/65 character reason", WorldActorDespawnWire(reason: Repeat("r", 65)));

            r.RejectsEncode("WorldActors/WorldActorSpawn/null", () => RealtimeProtocol.EncodeWorldActorSpawn(null));
            r.RejectsEncode("WorldActors/WorldActorSpawn/entity zero", () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(entityId: 0)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/empty runtime id",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(runtimeActorId: Guid.Empty)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/whitespace display name",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(displayName: " ")));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/undefined kind",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(kind: (RealtimeWorldActorKind)3)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/undefined disposition",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(disposition: 0)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/state revision 0",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(stateRevision: 0)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/bounds width 0",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(boundsSizeX: 0f)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/NaN position",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(positionX: float.NaN)));
            r.RejectsEncode(
                "WorldActors/WorldActorSpawn/undefined activity tier",
                () => RealtimeProtocol.EncodeWorldActorSpawn(WorldActorSpawnModel(activityTier: 0)));
            r.RejectsEncode("WorldActors/WorldActorState/null", () => RealtimeProtocol.EncodeWorldActorState(null));
            r.RejectsEncode(
                "WorldActors/WorldActorState/interaction revision 0",
                () => RealtimeProtocol.EncodeWorldActorState(new RealtimeWorldActorState(
                    1,
                    1,
                    0,
                    true,
                    0f,
                    0f,
                    0f,
                    0f,
                    RealtimeWorldActorActivityTier.Active)));
            r.RejectsEncode(
                "WorldActors/WorldActorState/undefined activity tier",
                () => RealtimeProtocol.EncodeWorldActorState(new RealtimeWorldActorState(1, 1, 1, true, 0f, 0f, 0f, 0f, 0)));
            r.RejectsEncode("WorldActors/WorldActorDespawn/null", () => RealtimeProtocol.EncodeWorldActorDespawn(null));
            r.RejectsEncode(
                "WorldActors/WorldActorDespawn/empty runtime id",
                () => RealtimeProtocol.EncodeWorldActorDespawn(new RealtimeWorldActorDespawn(1, Guid.Empty, "reason")));
            r.RejectsEncode(
                "WorldActors/WorldActorDespawn/whitespace reason",
                () => RealtimeProtocol.EncodeWorldActorDespawn(new RealtimeWorldActorDespawn(1, G(6), " ")));
        }

        private static void RegisterWorldInteractionIntentCases(Registry r)
        {
            const byte open = 1;
            const byte close = 2;
            const byte action = 3;

            r.Accepts("WorldActors/WorldInteractionIntent/open baseline", InteractionIntentWire(open));
            r.Accepts("WorldActors/WorldInteractionIntent/open corpse", InteractionIntentWire(open, targetKind: 2));
            r.AcceptsAs(
                "WorldActors/WorldInteractionIntent/open ignores the interaction session",
                InteractionIntentWire(open, interactionSessionId: G(9)),
                InteractionIntentWire(open));
            r.Rejects(
                "WorldActors/WorldInteractionIntent/open with a capability payload",
                InteractionIntentWire(open, capability: CapabilityPayloadWire()));
            r.Rejects("WorldActors/WorldInteractionIntent/empty operation id", InteractionIntentWire(open, operationId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionIntent/empty simulation session", InteractionIntentWire(open, simulationSessionId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionIntent/target kind 0", InteractionIntentWire(open, targetKind: 0));
            r.Rejects("WorldActors/WorldInteractionIntent/target kind 3", InteractionIntentWire(open, targetKind: 3));
            r.Rejects("WorldActors/WorldInteractionIntent/target entity zero", InteractionIntentWire(open, targetEntityId: 0));
            r.Rejects("WorldActors/WorldInteractionIntent/empty target runtime id", InteractionIntentWire(open, targetRuntimeId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionIntent/target revision 0", InteractionIntentWire(open, expectedTargetRevision: 0));
            r.Rejects("WorldActors/WorldInteractionIntent/operation kind 0", InteractionIntentWire(0, interactionSessionId: G(9)));
            r.Rejects("WorldActors/WorldInteractionIntent/operation kind 4", InteractionIntentWire(4, interactionSessionId: G(9)));

            r.Accepts("WorldActors/WorldInteractionIntent/close baseline", InteractionIntentWire(close, interactionSessionId: G(9)));
            r.Rejects("WorldActors/WorldInteractionIntent/close without session", InteractionIntentWire(close));

            Wire ActionWire(Wire capability, Guid? interactionSessionId = null)
            {
                return InteractionIntentWire(action, interactionSessionId: interactionSessionId ?? G(9), capability: capability);
            }

            r.Accepts("WorldActors/WorldInteractionIntent/action baseline", ActionWire(CapabilityPayloadWire()));
            r.Rejects("WorldActors/WorldInteractionIntent/action without session", ActionWire(CapabilityPayloadWire(), Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionIntent/action without payload", InteractionIntentWire(action, interactionSessionId: G(9)));
            r.Rejects("WorldActors/WorldInteractionIntent/action whitespace capability", ActionWire(CapabilityPayloadWire(capabilityId: " ")));
            r.Rejects("WorldActors/WorldInteractionIntent/action empty capability", ActionWire(CapabilityPayloadWire(capabilityId: "")));
            r.Accepts("WorldActors/WorldInteractionIntent/action 128 character capability", ActionWire(CapabilityPayloadWire(capabilityId: Repeat("c", 128))));
            r.Rejects("WorldActors/WorldInteractionIntent/action 129 character capability", ActionWire(CapabilityPayloadWire(capabilityId: Repeat("c", 129))));
            r.Rejects("WorldActors/WorldInteractionIntent/action capability kind 0", ActionWire(CapabilityPayloadWire(capabilityKind: 0)));
            r.Rejects("WorldActors/WorldInteractionIntent/action capability kind 10", ActionWire(CapabilityPayloadWire(capabilityKind: 10)));
            r.Rejects("WorldActors/WorldInteractionIntent/action capability revision 0", ActionWire(CapabilityPayloadWire(capabilityRevision: 0)));
            r.Rejects("WorldActors/WorldInteractionIntent/action lifecycle 5", ActionWire(CapabilityPayloadWire(lifecycle: 5)));
            r.Rejects("WorldActors/WorldInteractionIntent/no lifecycle with character revision", ActionWire(CapabilityPayloadWire(characterRevision: 1)));
            r.Rejects("WorldActors/WorldInteractionIntent/no lifecycle with item", ActionWire(CapabilityPayloadWire(item: G(2))));
            r.Rejects("WorldActors/WorldInteractionIntent/no lifecycle with destination", ActionWire(CapabilityPayloadWire(destination: G(3))));
            r.Rejects("WorldActors/WorldInteractionIntent/no lifecycle with slot 0", ActionWire(CapabilityPayloadWire(slot: 0)));

            foreach (var lifecycle in new byte[] { 1, 2 })
            {
                var prefix = "WorldActors/WorldInteractionIntent/insurance " + lifecycle + " ";
                Wire Insurance(
                    byte capabilityKind = 6,
                    long characterRevision = 11,
                    Guid? item = null,
                    long itemRevision = 6,
                    Guid? destination = null,
                    int slot = -1)
                {
                    return ActionWire(CapabilityPayloadWire(
                        capabilityId: "insurance.basic",
                        capabilityKind: capabilityKind,
                        capabilityRevision: 3,
                        lifecycle: lifecycle,
                        characterRevision: characterRevision,
                        item: item ?? G(2),
                        itemRevision: itemRevision,
                        destination: destination,
                        slot: slot));
                }

                r.Accepts(prefix + "baseline", Insurance());
                r.Accepts(prefix + "character revision 0", Insurance(characterRevision: 0));
                r.Rejects(prefix + "dialogue capability", Insurance(capabilityKind: 1));
                r.Rejects(prefix + "negative character revision", Insurance(characterRevision: -1));
                r.Rejects(prefix + "without item", Insurance(item: Guid.Empty));
                r.Rejects(prefix + "negative item revision", Insurance(itemRevision: -1));
                r.Rejects(prefix + "with destination", Insurance(destination: G(3)));
                r.Rejects(prefix + "with slot 0", Insurance(slot: 0));
            }

            Wire Quest(
                byte lifecycle,
                byte capabilityKind = 3,
                long characterRevision = 0,
                Guid? item = null,
                long itemRevision = 0,
                Guid? destination = null,
                int slot = -1)
            {
                return ActionWire(CapabilityPayloadWire(
                    capabilityId: "quest.first",
                    capabilityKind: capabilityKind,
                    capabilityRevision: 5,
                    lifecycle: lifecycle,
                    characterRevision: characterRevision,
                    item: item,
                    itemRevision: itemRevision,
                    destination: destination,
                    slot: slot));
            }

            r.Accepts("WorldActors/WorldInteractionIntent/accept quest baseline", Quest(3, destination: G(3)));
            r.Accepts("WorldActors/WorldInteractionIntent/accept quest into slot 4", Quest(3, destination: G(3), slot: 4));
            r.Rejects("WorldActors/WorldInteractionIntent/accept quest into slot -2", Quest(3, destination: G(3), slot: -2));
            r.Rejects("WorldActors/WorldInteractionIntent/accept quest without destination", Quest(3));
            r.Rejects("WorldActors/WorldInteractionIntent/accept quest with item", Quest(3, destination: G(3), item: G(2)));
            r.Rejects("WorldActors/WorldInteractionIntent/accept quest with item revision", Quest(3, destination: G(3), itemRevision: 1));
            r.Rejects("WorldActors/WorldInteractionIntent/accept quest at a turn-in", Quest(3, capabilityKind: 4, destination: G(3)));
            r.Rejects(
                "WorldActors/WorldInteractionIntent/accept quest with negative character revision",
                Quest(3, characterRevision: -1, destination: G(3)));
            r.Accepts("WorldActors/WorldInteractionIntent/abandon quest baseline", Quest(4, characterRevision: 8));
            r.Rejects("WorldActors/WorldInteractionIntent/abandon quest with destination", Quest(4, destination: G(3)));
            r.Rejects("WorldActors/WorldInteractionIntent/abandon quest with slot 0", Quest(4, slot: 0));
            r.Rejects("WorldActors/WorldInteractionIntent/abandon quest with item", Quest(4, item: G(2)));

            r.RejectsEncode("WorldActors/WorldInteractionIntent/null", () => RealtimeProtocol.EncodeWorldInteractionIntent(null));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/open without simulation session",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateOpen(
                    G(7),
                    Guid.Empty,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    ActorEntityId,
                    G(6),
                    4)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/open undefined target kind",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(
                    RealtimeWorldInteractionIntent.CreateOpen(G(7), G(8), 0, ActorEntityId, G(6), 4)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/close without interaction session",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateClose(
                    G(7),
                    G(8),
                    Guid.Empty,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    ActorEntityId,
                    G(6),
                    4)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/action whitespace capability",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    ActorEntityId,
                    G(6),
                    4,
                    " ",
                    RealtimeWorldActorCapabilityKind.Dialogue,
                    2)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/insurance at a dialogue capability",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    ActorEntityId,
                    G(6),
                    4,
                    "dialogue.greeting",
                    RealtimeWorldActorCapabilityKind.Dialogue,
                    2,
                    RealtimeNpcLifecycleActionKind.ApplyInsurance,
                    1,
                    G(2),
                    1)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionIntent/action capability revision 0",
                () => RealtimeProtocol.EncodeWorldInteractionIntent(RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    G(7),
                    G(8),
                    G(9),
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    ActorEntityId,
                    G(6),
                    4,
                    "dialogue.greeting",
                    RealtimeWorldActorCapabilityKind.Dialogue,
                    0)));
        }

        private static void RegisterWorldInteractionResponseCases(Registry r)
        {
            r.Accepts("WorldActors/WorldInteractionOpened/baseline", OpenedWire());
            r.Accepts("WorldActors/WorldInteractionOpened/no capabilities", OpenedWire(capabilities: new Wire[0]));
            r.Accepts(
                "WorldActors/WorldInteractionOpened/16 capabilities",
                OpenedWire(capabilities: Enumerable.Range(0, 16).Select(index => CapabilityWire()).ToArray()));
            r.Rejects(
                "WorldActors/WorldInteractionOpened/17 capabilities",
                OpenedWire(capabilities: Enumerable.Range(0, 17).Select(index => CapabilityWire()).ToArray()));
            r.Rejects("WorldActors/WorldInteractionOpened/empty operation id", OpenedWire(operationId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionOpened/empty interaction session", OpenedWire(interactionSessionId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionOpened/target kind 0", OpenedWire(targetKind: 0));
            r.Rejects("WorldActors/WorldInteractionOpened/target entity zero", OpenedWire(targetEntityId: 0));
            r.Rejects("WorldActors/WorldInteractionOpened/empty target runtime id", OpenedWire(targetRuntimeId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionOpened/target revision 0", OpenedWire(targetRevision: 0));
            r.Rejects("WorldActors/WorldInteractionOpened/summary revision 0", OpenedWire(capabilitySummaryRevision: 0));
            r.Rejects("WorldActors/WorldInteractionOpened/whitespace display name", OpenedWire(displayName: " "));
            r.Rejects("WorldActors/WorldInteractionOpened/whitespace capability id", OpenedWire(capabilities: new[] { CapabilityWire(id: " ") }));
            r.Rejects("WorldActors/WorldInteractionOpened/capability kind 0", OpenedWire(capabilities: new[] { CapabilityWire(kind: 0) }));
            r.Rejects("WorldActors/WorldInteractionOpened/capability kind 10", OpenedWire(capabilities: new[] { CapabilityWire(kind: 10) }));
            r.Rejects(
                "WorldActors/WorldInteractionOpened/whitespace capability name",
                OpenedWire(capabilities: new[] { CapabilityWire(displayName: " ") }));
            r.Rejects(
                "WorldActors/WorldInteractionOpened/129 character capability name",
                OpenedWire(capabilities: new[] { CapabilityWire(displayName: Repeat("n", 129)) }));
            r.Rejects("WorldActors/WorldInteractionOpened/capability revision 0", OpenedWire(capabilities: new[] { CapabilityWire(revision: 0) }));
            r.AcceptsAs(
                "WorldActors/WorldInteractionOpened/available byte 2 reads as true",
                OpenedWire(capabilities: new[] { CapabilityWire(isAvailable: 2) }),
                OpenedWire(capabilities: new[] { CapabilityWire(isAvailable: 1) }));
            r.Accepts(
                "WorldActors/WorldInteractionOpened/duplicate capability ids",
                OpenedWire(capabilities: new[] { CapabilityWire(), CapabilityWire() }));

            r.Accepts("WorldActors/WorldInteractionResult/close baseline", InteractionResultWire());
            r.Accepts("WorldActors/WorldInteractionResult/empty message", InteractionResultWire(message: ""));
            r.Accepts("WorldActors/WorldInteractionResult/whitespace message", InteractionResultWire(message: "  "));
            r.Accepts("WorldActors/WorldInteractionResult/512 character message", InteractionResultWire(message: Repeat("m", 512)));
            r.Rejects("WorldActors/WorldInteractionResult/513 character message", InteractionResultWire(message: Repeat("m", 513)));
            r.Rejects("WorldActors/WorldInteractionResult/invalid UTF-8 message", InteractionResultWire(message: WireText.Raw(0xFF)));
            r.Rejects(
                "WorldActors/WorldInteractionResult/message length above four bytes per character",
                InteractionResultWire(message: WireText.Declared(2049, 0x6D)));
            r.Accepts(
                "WorldActors/WorldInteractionResult/open failure",
                InteractionResultWire(
                    operationKind: 1,
                    interactionSessionId: Guid.Empty,
                    succeeded: 0,
                    targetRevision: 0,
                    error: ErrorFragment("interaction_out_of_range", "Move closer.")));
            r.Rejects(
                "WorldActors/WorldInteractionResult/open success",
                InteractionResultWire(operationKind: 1, interactionSessionId: Guid.Empty));
            r.Rejects(
                "WorldActors/WorldInteractionResult/open failure with session",
                InteractionResultWire(operationKind: 1, succeeded: 0, error: ErrorFragment("code", "message")));
            r.Rejects("WorldActors/WorldInteractionResult/close without session", InteractionResultWire(interactionSessionId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionResult/success with target revision 0", InteractionResultWire(targetRevision: 0));
            r.Accepts(
                "WorldActors/WorldInteractionResult/failure with target revision 0",
                InteractionResultWire(succeeded: 0, targetRevision: 0, error: ErrorFragment("code", "message")));
            r.Rejects(
                "WorldActors/WorldInteractionResult/negative target revision",
                InteractionResultWire(succeeded: 0, targetRevision: -1, error: ErrorFragment("code", "message")));
            r.Rejects("WorldActors/WorldInteractionResult/negative item state revision", InteractionResultWire(itemStateRevision: -1));
            r.Rejects(
                "WorldActors/WorldInteractionResult/failure with whitespace code",
                InteractionResultWire(succeeded: 0, error: ErrorFragment(" ", "message")));
            r.Rejects("WorldActors/WorldInteractionResult/operation kind 0", InteractionResultWire(operationKind: 0));
            r.Rejects("WorldActors/WorldInteractionResult/operation kind 4", InteractionResultWire(operationKind: 4));
            r.Rejects("WorldActors/WorldInteractionResult/empty operation id", InteractionResultWire(operationId: Guid.Empty));
            r.AcceptsAs(
                "WorldActors/WorldInteractionResult/succeeded byte 2 reads as true",
                InteractionResultWire(succeeded: 2),
                InteractionResultWire());

            r.Accepts("WorldActors/WorldInteractionClosed/baseline", InteractionClosedWire());
            r.Rejects("WorldActors/WorldInteractionClosed/empty session", InteractionClosedWire(interactionSessionId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionClosed/target kind 3", InteractionClosedWire(targetKind: 3));
            r.Rejects("WorldActors/WorldInteractionClosed/target entity zero", InteractionClosedWire(targetEntityId: 0));
            r.Rejects("WorldActors/WorldInteractionClosed/empty target runtime id", InteractionClosedWire(targetRuntimeId: Guid.Empty));
            r.Rejects("WorldActors/WorldInteractionClosed/whitespace code", InteractionClosedWire(code: " "));
            r.Rejects("WorldActors/WorldInteractionClosed/whitespace message", InteractionClosedWire(message: " "));
            r.Rejects("WorldActors/WorldInteractionClosed/65 character code", InteractionClosedWire(code: Repeat("c", 65)));

            r.RejectsEncode("WorldActors/WorldInteractionOpened/null", () => RealtimeProtocol.EncodeWorldInteractionOpened(null));
            r.RejectsEncode(
                "WorldActors/WorldInteractionOpened/17 capabilities",
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(Enumerable.Range(0, 17)
                    .Select(index => new RealtimeWorldActorCapability("c", RealtimeWorldActorCapabilityKind.Vendor, "n", true, 1))
                    .ToArray())));
            r.RejectsEncode(
                "WorldActors/WorldInteractionOpened/null capability",
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(new RealtimeWorldActorCapability[] { null })));
            r.RejectsEncode(
                "WorldActors/WorldInteractionOpened/whitespace capability name",
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(new[]
                {
                    new RealtimeWorldActorCapability("c", RealtimeWorldActorCapabilityKind.Vendor, " ", true, 1)
                })));
            r.RejectsOversize(
                "WorldActors/WorldInteractionOpened/long capabilities above the packet limit",
                () => RealtimeProtocol.EncodeWorldInteractionOpened(OpenedModel(Enumerable.Range(0, 16)
                    .Select(index => new RealtimeWorldActorCapability(
                        Repeat("c", 128),
                        RealtimeWorldActorCapabilityKind.Vendor,
                        Repeat("n", 128),
                        true,
                        1))
                    .ToArray())));
            r.RejectsEncode("WorldActors/WorldInteractionResult/null", () => RealtimeProtocol.EncodeWorldInteractionResult(null));
            r.RejectsEncode(
                "WorldActors/WorldInteractionResult/open success",
                () => RealtimeProtocol.EncodeWorldInteractionResult(InteractionResultModel(
                    interactionSessionId: Guid.Empty,
                    operationKind: RealtimeWorldInteractionOperationKind.Open)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionResult/failure without error",
                () => RealtimeProtocol.EncodeWorldInteractionResult(InteractionResultModel(succeeded: false)));
            r.RejectsEncode(
                "WorldActors/WorldInteractionResult/success with error",
                () => RealtimeProtocol.EncodeWorldInteractionResult(InteractionResultModel(
                    error: new RealtimeError("code", "message"))));
            r.RejectsEncode(
                "WorldActors/WorldInteractionResult/513 character message",
                () => RealtimeProtocol.EncodeWorldInteractionResult(InteractionResultModel(message: Repeat("m", 513))));
            r.RejectsEncode(
                "WorldActors/WorldInteractionResult/negative item state revision",
                () => RealtimeProtocol.EncodeWorldInteractionResult(InteractionResultModel(itemStateRevision: -1)));
            r.RejectsEncode("WorldActors/WorldInteractionClosed/null", () => RealtimeProtocol.EncodeWorldInteractionClosed(null));
            r.RejectsEncode(
                "WorldActors/WorldInteractionClosed/whitespace code",
                () => RealtimeProtocol.EncodeWorldInteractionClosed(new RealtimeWorldInteractionClosed(
                    G(9),
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    1,
                    G(6),
                    " ",
                    "message")));
        }

        private static RealtimeWorldActorSpawn WorldActorSpawnModel(
            ulong entityId = ActorEntityId,
            Guid? runtimeActorId = null,
            string displayName = "Smeden Åke",
            RealtimeWorldActorKind kind = RealtimeWorldActorKind.Npc,
            RealtimeWorldActorDisposition disposition = RealtimeWorldActorDisposition.Friendly,
            float positionX = 10.5f,
            long stateRevision = 3,
            bool isActive = true,
            float boundsSizeX = 0.8f,
            RealtimeWorldActorActivityTier activityTier = RealtimeWorldActorActivityTier.EventDriven)
        {
            return new RealtimeWorldActorSpawn(
                entityId: entityId,
                runtimeActorId: runtimeActorId ?? G(6),
                actorDefinitionId: "npc.blacksmith",
                spawnDefinitionId: "spawn.blacksmith.1",
                displayName: displayName,
                kind: kind,
                factionId: "faction.town",
                disposition: disposition,
                presentationArchetypeId: "presentation.blacksmith",
                positionX: positionX,
                positionY: 0.25f,
                positionZ: -20.75f,
                yawDegrees: 90f,
                stateRevision: stateRevision,
                interactionRevision: 4,
                isActive: isActive,
                boundsCenterX: 10.5f,
                boundsCenterY: 1f,
                boundsCenterZ: -20.75f,
                boundsSizeX: boundsSizeX,
                boundsSizeY: 2f,
                boundsSizeZ: 0.75f,
                activityTier: activityTier);
        }

        private static RealtimeWorldInteractionResult InteractionResultModel(
            Guid? interactionSessionId = null,
            RealtimeWorldInteractionOperationKind operationKind = RealtimeWorldInteractionOperationKind.Close,
            bool succeeded = true,
            RealtimeError error = null,
            string message = "Farväl",
            long itemStateRevision = 0)
        {
            return new RealtimeWorldInteractionResult(
                operationId: G(7),
                interactionSessionId: interactionSessionId ?? G(9),
                operationKind: operationKind,
                succeeded: succeeded,
                targetRevision: 4,
                error: error,
                message: message,
                itemStateRevision: itemStateRevision);
        }

        private static RealtimeWorldInteractionOpened OpenedModel(RealtimeWorldActorCapability[] capabilities)
        {
            return new RealtimeWorldInteractionOpened(
                operationId: G(7),
                interactionSessionId: G(9),
                targetKind: RealtimeWorldInteractionTargetKind.WorldActor,
                targetEntityId: ActorEntityId,
                targetRuntimeId: G(6),
                targetRevision: 4,
                capabilitySummaryRevision: 5,
                displayName: "Smeden Åke",
                capabilities: capabilities);
        }

        private static Wire ActorStatePayloadWire(
            long stateRevision = 3,
            long interactionRevision = 4,
            byte isActive = 1,
            uint? positionXBits = null,
            byte activityTier = 1)
        {
            return Wire.Fragment()
                .I64(stateRevision)
                .I64(interactionRevision)
                .U8(isActive)
                .F32Or(10.5f, positionXBits)
                .F32(0.25f)
                .F32(-20.75f)
                .F32(90f)
                .U8(activityTier);
        }

        private static Wire WorldActorSpawnWire(
            ulong entityId = ActorEntityId,
            Guid? runtimeActorId = null,
            WireText actorDefinitionId = null,
            WireText spawnDefinitionId = null,
            WireText displayName = null,
            byte kind = 1,
            WireText factionId = null,
            byte disposition = 1,
            WireText presentationArchetypeId = null,
            Wire payload = null,
            float boundsCenterX = 10.5f,
            uint? boundsCenterXBits = null,
            float boundsSizeX = 0.8f,
            float boundsSizeY = 2f,
            uint? boundsSizeZBits = null)
        {
            return Wire.Message(RealtimeMessageType.WorldActorSpawn)
                .U64(entityId)
                .Guid(runtimeActorId ?? G(6))
                .Text(actorDefinitionId ?? "npc.blacksmith")
                .Text(spawnDefinitionId ?? "spawn.blacksmith.1")
                .Text(displayName ?? "Smeden Åke")
                .U8(kind)
                .Text(factionId ?? "faction.town")
                .U8(disposition)
                .Text(presentationArchetypeId ?? "presentation.blacksmith")
                .Append(payload ?? ActorStatePayloadWire())
                .F32Or(boundsCenterX, boundsCenterXBits)
                .F32(1f)
                .F32(-20.75f)
                .F32(boundsSizeX)
                .F32(boundsSizeY)
                .F32Or(0.75f, boundsSizeZBits);
        }

        private static Wire WorldActorStateWire(ulong entityId = ActorEntityId, Wire payload = null)
        {
            return Wire.Message(RealtimeMessageType.WorldActorState).U64(entityId).Append(payload ?? ActorStatePayloadWire());
        }

        private static Wire WorldActorDespawnWire(ulong entityId = ActorEntityId, Guid? runtimeActorId = null, WireText reason = null)
        {
            return Wire.Message(RealtimeMessageType.WorldActorDespawn)
                .U64(entityId)
                .Guid(runtimeActorId ?? G(6))
                .Text(reason ?? "despawned");
        }

        private static Wire InteractionIntentWire(
            byte operationKind,
            Guid? operationId = null,
            Guid? simulationSessionId = null,
            Guid? interactionSessionId = null,
            byte targetKind = 1,
            ulong targetEntityId = ActorEntityId,
            Guid? targetRuntimeId = null,
            long expectedTargetRevision = 4,
            Wire capability = null)
        {
            return Wire.Message(RealtimeMessageType.WorldInteractionIntent)
                .Guid(operationId ?? G(7))
                .Guid(simulationSessionId ?? G(8))
                .Guid(interactionSessionId ?? Guid.Empty)
                .U8(operationKind)
                .U8(targetKind)
                .U64(targetEntityId)
                .Guid(targetRuntimeId ?? G(6))
                .I64(expectedTargetRevision)
                .Append(capability ?? Wire.Fragment());
        }

        private static Wire CapabilityPayloadWire(
            WireText capabilityId = null,
            byte capabilityKind = 1,
            long capabilityRevision = 2,
            byte lifecycle = 0,
            long characterRevision = 0,
            Guid? item = null,
            long itemRevision = 0,
            Guid? destination = null,
            int slot = -1)
        {
            return Wire.Fragment()
                .Text(capabilityId ?? "dialogue.greeting")
                .U8(capabilityKind)
                .I64(capabilityRevision)
                .U8(lifecycle)
                .I64(characterRevision)
                .Guid(item ?? Guid.Empty)
                .I64(itemRevision)
                .Guid(destination ?? Guid.Empty)
                .I32(slot);
        }

        private static Wire CapabilityWire(
            WireText id = null,
            byte kind = 1,
            WireText displayName = null,
            byte isAvailable = 1,
            long revision = 2)
        {
            return Wire.Fragment()
                .Text(id ?? "dialogue.greeting")
                .U8(kind)
                .Text(displayName ?? "Prata")
                .U8(isAvailable)
                .I64(revision);
        }

        private static Wire OpenedWire(
            Guid? operationId = null,
            Guid? interactionSessionId = null,
            byte targetKind = 1,
            ulong targetEntityId = ActorEntityId,
            Guid? targetRuntimeId = null,
            long targetRevision = 4,
            long capabilitySummaryRevision = 5,
            WireText displayName = null,
            Wire[] capabilities = null)
        {
            var list = capabilities ?? new[] { CapabilityWire(), CapabilityWire(id: "vendor.tools", kind: 2, isAvailable: 0) };
            var wire = Wire.Message(RealtimeMessageType.WorldInteractionOpened)
                .Guid(operationId ?? G(7))
                .Guid(interactionSessionId ?? G(9))
                .U8(targetKind)
                .U64(targetEntityId)
                .Guid(targetRuntimeId ?? G(6))
                .I64(targetRevision)
                .I64(capabilitySummaryRevision)
                .Text(displayName ?? "Smeden Åke")
                .U8((byte)list.Length);
            foreach (var capability in list)
            {
                wire.Append(capability);
            }

            return wire;
        }

        private static Wire InteractionResultWire(
            Guid? operationId = null,
            Guid? interactionSessionId = null,
            byte operationKind = 2,
            byte succeeded = 1,
            long targetRevision = 4,
            long itemStateRevision = 0,
            WireText message = null,
            Wire error = null)
        {
            return Wire.Message(RealtimeMessageType.WorldInteractionResult)
                .Guid(operationId ?? G(7))
                .Guid(interactionSessionId ?? G(9))
                .U8(operationKind)
                .U8(succeeded)
                .I64(targetRevision)
                .I64(itemStateRevision)
                .Text(message ?? "Farväl")
                .Append(error ?? Wire.Fragment());
        }

        private static Wire InteractionClosedWire(
            Guid? interactionSessionId = null,
            byte targetKind = 1,
            ulong targetEntityId = ActorEntityId,
            Guid? targetRuntimeId = null,
            WireText code = null,
            WireText message = null)
        {
            return Wire.Message(RealtimeMessageType.WorldInteractionClosed)
                .Guid(interactionSessionId ?? G(9))
                .U8(targetKind)
                .U64(targetEntityId)
                .Guid(targetRuntimeId ?? G(6))
                .Text(code ?? "target_despawned")
                .Text(message ?? "The target is gone.");
        }
    }
}
