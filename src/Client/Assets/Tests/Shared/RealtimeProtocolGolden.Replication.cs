#nullable disable
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // Entity presence and the shared simulation snapshot.
    public static partial class RealtimeProtocolGolden
    {
        private const string PersistentPlayerId = "8c3d2e1f-0b4a-4c5d-9e6f-a1b2c3d4e5f6";

        private static void RegisterReplication(Registry r)
        {
            const RealtimeMessageType entitySpawn = RealtimeMessageType.EntitySpawn;
            const RealtimeMessageType entityDespawn = RealtimeMessageType.EntityDespawn;
            const RealtimeMessageType simulationSnapshot = RealtimeMessageType.SimulationSnapshot;
            const RealtimeMessageType appearanceChanged = RealtimeMessageType.EntityAppearanceChanged;
            const RealtimeMessageType appearanceIntent = RealtimeMessageType.AppearanceIntent;
            const RealtimeMessageType appearanceResult = RealtimeMessageType.AppearanceIntentResult;

            r.Vector("Replication/EntitySpawn/typical", entitySpawn, () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel()));
            r.Vector(
                "Replication/EntitySpawn/braced persistent id",
                entitySpawn,
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(
                    persistentId: "{8C3D2E1F-0B4A-4C5D-9E6F-A1B2C3D4E5F6}",
                    state: PlayerStateBoundaryModel())));
            r.Vector(
                "Replication/EntityDespawn/typical",
                entityDespawn,
                () => RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(entityId: 7, reason: "out_of_interest")));
            r.Vector(
                "Replication/SimulationSnapshot/single entity",
                simulationSnapshot,
                () => RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(
                    snapshotSequence: 0x01020304,
                    serverTick: 0x05060708,
                    chunkIndex: 0,
                    chunkCount: 1,
                    entities: new[] { EntitySnapshotModel(42) })));
            r.Vector(
                "Replication/SimulationSnapshot/full chunk",
                simulationSnapshot,
                () => RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(
                    snapshotSequence: uint.MaxValue,
                    serverTick: 0,
                    chunkIndex: 2,
                    chunkCount: 3,
                    entities: Enumerable.Range(1, 71).Select(index => EntitySnapshotModel((uint)index * 1000003)).ToArray())));
            r.Vector(
                "Replication/SimulationSnapshot/range edges",
                simulationSnapshot,
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    new RealtimeEntitySnapshot(uint.MaxValue, -8388608, short.MinValue, 8388607, 1023, false),
                    new RealtimeEntitySnapshot(1, 8388607, short.MaxValue, -8388608, 0, true))));

            r.Vector(
                "Replication/EntityAppearanceChanged/typical",
                appearanceChanged,
                () => RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(7, AppearanceModel())));
            r.Vector(
                "Replication/EntityAppearanceChanged/bare",
                appearanceChanged,
                () => RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(
                    7,
                    new RealtimeCharacterAppearance("Human_Male", null, null, false, null))));
            r.Vector(
                "Replication/AppearanceIntent/lower headwear",
                appearanceIntent,
                () => RealtimeProtocol.EncodeAppearanceIntent(RealtimeAppearanceIntent.CreateLowerHeadwear(G(1))));
            r.Vector(
                "Replication/AppearanceIntent/raise headwear",
                appearanceIntent,
                () => RealtimeProtocol.EncodeAppearanceIntent(RealtimeAppearanceIntent.CreateRaiseHeadwear(G(1))));
            r.Vector(
                "Replication/AppearanceIntent/set development look",
                appearanceIntent,
                () => RealtimeProtocol.EncodeAppearanceIntent(RealtimeAppearanceIntent.CreateSetDevelopmentLook(
                    G(1),
                    "Human_Female",
                    AppearanceModel().Choices)));
            r.Vector(
                "Replication/AppearanceIntentResult/accepted",
                appearanceResult,
                () => RealtimeProtocol.EncodeAppearanceIntentResult(new RealtimeAppearanceIntentResult(
                    G(1),
                    RealtimeAppearanceIntentKind.LowerHeadwear,
                    true,
                    null)));
            r.Vector(
                "Replication/AppearanceIntentResult/rejected",
                appearanceResult,
                () => RealtimeProtocol.EncodeAppearanceIntentResult(new RealtimeAppearanceIntentResult(
                    G(1),
                    RealtimeAppearanceIntentKind.SetDevelopmentLook,
                    false,
                    new RealtimeError("development_look_disabled", "Only a Development GameService changes a look."))));

            r.Accepts("Replication/EntitySpawn/baseline", EntitySpawnWire());
            r.Rejects(
                "Replication/EntitySpawn/empty body model",
                EntitySpawnWire(appearance: AppearanceWire(bodyModel: "")));
            r.Rejects(
                "Replication/EntitySpawn/duplicate equipment slot",
                EntitySpawnWire(appearance: AppearanceWire(equipment: new[] { ("back", "clothing.wool_cloak"), ("back", "clothing.red_wool_cloak") })));
            r.Accepts(
                "Replication/EntitySpawn/eight equipped items",
                EntitySpawnWire(appearance: AppearanceWire(equipment: Enumerable.Range(1, 8).Select(index => ("slot" + index, "item." + index)).ToArray())));
            r.Rejects(
                "Replication/EntitySpawn/nine equipped items",
                EntitySpawnWire(appearance: AppearanceWire(equipment: Enumerable.Range(1, 9).Select(index => ("slot" + index, "item." + index)).ToArray())));
            r.Accepts("Replication/EntityAppearanceChanged/baseline", Wire.Message(appearanceChanged).U64(7).Append(AppearanceWire()));
            r.Rejects("Replication/EntityAppearanceChanged/entity zero", Wire.Message(appearanceChanged).U64(0).Append(AppearanceWire()));
            r.Rejects(
                "Replication/EntityAppearanceChanged/blank choice kind",
                Wire.Message(appearanceChanged).U64(7).Append(AppearanceWire(choices: new[] { (" ", "", "Clean") })));
            r.Accepts(
                "Replication/EntityAppearanceChanged/no tool in use",
                Wire.Message(appearanceChanged).U64(7).Append(AppearanceWire(toolInUse: null)));
            r.Rejects(
                "Replication/EntityAppearanceChanged/129 character tool in use",
                Wire.Message(appearanceChanged).U64(7).Append(AppearanceWire(toolInUse: Repeat("t", 129))));
            r.Accepts(
                "Replication/AppearanceIntent/lower headwear",
                Wire.Message(appearanceIntent).Guid(G(1)).U8(1));
            r.Rejects("Replication/AppearanceIntent/kind 0", Wire.Message(appearanceIntent).Guid(G(1)).U8(0));
            r.Rejects("Replication/AppearanceIntent/kind 4", Wire.Message(appearanceIntent).Guid(G(1)).U8(4));
            r.Rejects("Replication/AppearanceIntent/empty operation", Wire.Message(appearanceIntent).Guid(System.Guid.Empty).U8(2));
            r.Rejects("Replication/EntitySpawn/entity zero", EntitySpawnWire(entityId: 0));
            r.Rejects("Replication/EntitySpawn/kind 0", EntitySpawnWire(kind: 0));
            r.Rejects("Replication/EntitySpawn/kind 2", EntitySpawnWire(kind: 2));
            r.Rejects("Replication/EntitySpawn/persistent id not a GUID", EntitySpawnWire(persistentId: "player-1"));
            r.Rejects("Replication/EntitySpawn/whitespace persistent id", EntitySpawnWire(persistentId: " "));
            r.Accepts(
                "Replication/EntitySpawn/persistent id without dashes",
                EntitySpawnWire(persistentId: "8c3d2e1f0b4a4c5d9e6fa1b2c3d4e5f6"));
            r.Accepts(
                "Replication/EntitySpawn/braced persistent id",
                EntitySpawnWire(persistentId: "{8C3D2E1F-0B4A-4C5D-9E6F-A1B2C3D4E5F6}"));
            r.Rejects("Replication/EntitySpawn/whitespace display name", EntitySpawnWire(displayName: "  "));
            r.Accepts("Replication/EntitySpawn/128 character display name", EntitySpawnWire(displayName: Repeat("n", 128)));
            r.Rejects("Replication/EntitySpawn/129 character display name", EntitySpawnWire(displayName: Repeat("n", 129)));
            r.Rejects("Replication/EntitySpawn/whitespace archetype", EntitySpawnWire(archetypeId: " "));
            r.Rejects("Replication/EntitySpawn/empty archetype", EntitySpawnWire(archetypeId: ""));
            r.Rejects("Replication/EntitySpawn/invalid state flags", EntitySpawnWire(state: PlayerStateWire(flags: 4)));

            r.Accepts("Replication/EntityDespawn/baseline", EntityDespawnWire());
            r.Rejects("Replication/EntityDespawn/entity zero", EntityDespawnWire(entityId: 0));
            r.Rejects("Replication/EntityDespawn/whitespace reason", EntityDespawnWire(reason: " "));
            r.Rejects("Replication/EntityDespawn/empty reason", EntityDespawnWire(reason: ""));
            r.Accepts("Replication/EntityDespawn/64 character reason", EntityDespawnWire(reason: Repeat("r", 64)));
            r.Rejects("Replication/EntityDespawn/65 character reason", EntityDespawnWire(reason: Repeat("r", 65)));

            r.Accepts("Replication/SimulationSnapshot/baseline", SimulationSnapshotWire(0, 1, EntitySnapshotWire()));
            r.Accepts(
                "Replication/SimulationSnapshot/negative positions",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(x: -8388608, y: -1, z: -1)));
            r.Accepts(
                "Replication/SimulationSnapshot/largest yaw step on ground",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(pose: 0x07FF)));
            r.Rejects(
                "Replication/SimulationSnapshot/unknown pose bit",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(pose: 0x0800)));
            r.Rejects(
                "Replication/SimulationSnapshot/high pose bit",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(pose: 0x8000)));
            r.Accepts(
                "Replication/SimulationSnapshot/last chunk of the largest count",
                SimulationSnapshotWire(65534, 65535, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/chunk count 0", SimulationSnapshotWire(0, 0, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/chunk index equal to count", SimulationSnapshotWire(1, 1, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/no entities", SimulationSnapshotWire(0, 1));
            r.Accepts(
                "Replication/SimulationSnapshot/71 entities",
                SimulationSnapshotWire(0, 1, Enumerable.Range(1, 71).Select(index => EntitySnapshotWire((uint)index)).ToArray()));
            r.Rejects(
                "Replication/SimulationSnapshot/72 entities",
                SimulationSnapshotWire(0, 1, Enumerable.Range(1, 72).Select(index => EntitySnapshotWire((uint)index)).ToArray()));
            r.Rejects("Replication/SimulationSnapshot/entity zero", SimulationSnapshotWire(0, 1, EntitySnapshotWire(0)));
            r.Accepts(
                "Replication/SimulationSnapshot/duplicate entities",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(5), EntitySnapshotWire(5)));

            r.RejectsEncode("Replication/EntitySpawn/null spawn", () => RealtimeProtocol.EncodeEntitySpawn(null));
            r.RejectsEncode("Replication/EntitySpawn/entity zero", () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(entityId: 0)));
            r.RejectsEncode(
                "Replication/EntitySpawn/undefined kind",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(kind: (RealtimeEntityKind)2)));
            r.RejectsEncode(
                "Replication/EntitySpawn/persistent id not a GUID",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(persistentId: "player-1")));
            r.RejectsEncode(
                "Replication/EntitySpawn/whitespace display name",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(displayName: " ")));
            r.RejectsEncode(
                "Replication/EntitySpawn/129 character archetype",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(archetypeId: Repeat("a", 129))));
            r.RejectsEncode(
                "Replication/EntitySpawn/null state",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(state: Null<RealtimePlayerState>())));
            r.RejectsEncode(
                "Replication/EntitySpawn/null appearance",
                () => RealtimeProtocol.EncodeEntitySpawn(EntitySpawnModel(appearance: Null<RealtimeCharacterAppearance>())));
            r.RejectsEncode(
                "Replication/EntityAppearanceChanged/65 character body model",
                () => RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(
                    7,
                    new RealtimeCharacterAppearance(Repeat("b", 65), null, null, false, null))));
            r.RejectsEncode(
                "Replication/EntityAppearanceChanged/129 character tool in use",
                () => RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(
                    7,
                    new RealtimeCharacterAppearance("Human_Male", null, null, false, Repeat("t", 129)))));
            r.RejectsEncode(
                "Replication/AppearanceIntent/blank development body model",
                () => RealtimeProtocol.EncodeAppearanceIntent(RealtimeAppearanceIntent.CreateSetDevelopmentLook(
                    G(1),
                    " ",
                    System.Array.Empty<RealtimeAppearanceChoice>())));
            r.RejectsEncode("Replication/EntityDespawn/null despawn", () => RealtimeProtocol.EncodeEntityDespawn(null));
            r.RejectsEncode(
                "Replication/EntityDespawn/entity zero",
                () => RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(0, "reason")));
            r.RejectsEncode(
                "Replication/EntityDespawn/whitespace reason",
                () => RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(1, " ")));
            r.RejectsEncode(
                "Replication/EntityDespawn/65 character reason",
                () => RealtimeProtocol.EncodeEntityDespawn(new RealtimeEntityDespawn(1, Repeat("r", 65))));
            r.RejectsEncode("Replication/SimulationSnapshot/null snapshot", () => RealtimeProtocol.EncodeSimulationSnapshot(null));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/chunk count 0",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 0, EntitySnapshotModel(1))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/chunk index equal to count",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(2, 2, EntitySnapshotModel(1))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/null entities",
                () => RealtimeProtocol.EncodeSimulationSnapshot(new RealtimeSimulationSnapshot(1, 2, 0, 1, null)));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/no entities",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 1)));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/72 entities",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    Enumerable.Range(1, 72).Select(index => EntitySnapshotModel((uint)index)).ToArray())));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/null entity",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 1, new RealtimeEntitySnapshot[] { null })));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/entity zero",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 1, EntitySnapshotModel(0))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/x above 24 bits",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    new RealtimeEntitySnapshot(1, 8388608, 0, 0, 0, true))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/z below 24 bits",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    new RealtimeEntitySnapshot(1, 0, 0, -8388609, 0, true))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/yaw step 1024",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    new RealtimeEntitySnapshot(1, 0, 0, 0, 1024, true))));
        }

        private static RealtimeEntitySpawn EntitySpawnModel(
            ulong entityId = 0x0A0B0C0D0E0F1011,
            RealtimeEntityKind kind = RealtimeEntityKind.Player,
            string persistentId = PersistentPlayerId,
            string displayName = "Grön Ödla",
            string archetypeId = "player-default",
            Optional<RealtimePlayerState> state = default,
            Optional<RealtimeCharacterAppearance> appearance = default)
        {
            return new RealtimeEntitySpawn(
                entityId: entityId,
                kind: kind,
                persistentId: persistentId,
                displayName: displayName,
                archetypeId: archetypeId,
                serverTick: 123456,
                initialState: state.Or(PlayerStateModel()),
                appearance: appearance.Or(AppearanceModel()));
        }

        private static RealtimeCharacterAppearance AppearanceModel()
        {
            return new RealtimeCharacterAppearance(
                bodyModel: "Human_Female",
                choices: new[]
                {
                    new RealtimeAppearanceChoice("face", "", "Freckles"),
                    new RealtimeAppearanceChoice("underwear", "legs", "Briefs"),
                },
                equipment: new[]
                {
                    new RealtimeVisibleEquipment("head", "clothing.blue_wool_hood"),
                    new RealtimeVisibleEquipment("back", "clothing.wool_cloak"),
                },
                headwearLowered: true,
                toolInUse: "tool.starter_pickaxe");
        }

        private static Wire AppearanceWire(
            WireText bodyModel = null,
            (string Kind, string Slot, string Option)[] choices = null,
            (string Slot, string Definition)[] equipment = null,
            bool headwearLowered = true,
            string toolInUse = "tool.starter_pickaxe")
        {
            choices = choices ?? new[] { ("face", "", "Freckles"), ("underwear", "legs", "Briefs") };
            equipment = equipment ?? new[] { ("head", "clothing.blue_wool_hood"), ("back", "clothing.wool_cloak") };
            var wire = Wire.Fragment().Text(bodyModel ?? "Human_Female").U8((byte)choices.Length);
            foreach (var choice in choices)
            {
                wire.Text(choice.Kind).Bool(choice.Slot.Length > 0);
                if (choice.Slot.Length > 0)
                {
                    wire.Text(choice.Slot);
                }

                wire.Text(choice.Option);
            }

            wire.U8((byte)equipment.Length);
            foreach (var item in equipment)
            {
                wire.Text(item.Slot).Text(item.Definition);
            }

            wire.Bool(headwearLowered).Bool(toolInUse != null);
            return toolInUse != null ? wire.Text(toolInUse) : wire;
        }

        // Spread positions and facings that differ for every id.
        private static RealtimeEntitySnapshot EntitySnapshotModel(uint entityId)
        {
            return new RealtimeEntitySnapshot(
                entityId: entityId,
                quantizedX: (int)(entityId % 8388607) - 4194304,
                quantizedY: (short)((int)(entityId % 65535) - 32768),
                quantizedZ: 4194304 - (int)(entityId % 8388607),
                yawStep: (ushort)(entityId % 1024),
                isGrounded: entityId % 2 == 0);
        }

        private static RealtimeSimulationSnapshot SimulationSnapshotModel(
            ushort chunkIndex,
            ushort chunkCount,
            params RealtimeEntitySnapshot[] entities)
        {
            return new RealtimeSimulationSnapshot(
                snapshotSequence: 11,
                serverTick: 22,
                chunkIndex: chunkIndex,
                chunkCount: chunkCount,
                entities: entities);
        }

        private static Wire EntitySpawnWire(
            ulong entityId = 0x0A0B0C0D0E0F1011,
            byte kind = 1,
            WireText persistentId = null,
            WireText displayName = null,
            WireText archetypeId = null,
            Wire state = null,
            Wire appearance = null)
        {
            return Wire.Message(RealtimeMessageType.EntitySpawn)
                .U64(entityId)
                .U8(kind)
                .Text(persistentId ?? PersistentPlayerId)
                .Text(displayName ?? "Grön Ödla")
                .Text(archetypeId ?? "player-default")
                .U32(123456)
                .Append(state ?? PlayerStateWire())
                .Append(appearance ?? AppearanceWire());
        }

        private static Wire EntityDespawnWire(ulong entityId = 7, WireText reason = null)
        {
            return Wire.Message(RealtimeMessageType.EntityDespawn).U64(entityId).Text(reason ?? "out_of_interest");
        }

        private static Wire EntitySnapshotWire(uint entityId = 42, int x = 1280, short y = -96, int z = -2560, ushort pose = 0x0500)
        {
            return Wire.Fragment()
                .U32(entityId)
                .I24(x)
                .I16(y)
                .I24(z)
                .U16(pose);
        }

        private static Wire SimulationSnapshotWire(ushort chunkIndex, ushort chunkCount, params Wire[] entities)
        {
            var wire = Wire.Message(RealtimeMessageType.SimulationSnapshot)
                .U32(0x01020304)
                .U32(0x05060708)
                .U16(chunkIndex)
                .U16(chunkCount)
                .U8((byte)entities.Length);
            foreach (var entity in entities)
            {
                wire.Append(entity);
            }

            return wire;
        }
    }
}
