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
                    entities: Enumerable.Range(1, 24).Select(index => EntitySnapshotModel((ulong)index * 1000003)).ToArray())));

            r.Accepts("Replication/EntitySpawn/baseline", EntitySpawnWire());
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
                "Replication/SimulationSnapshot/last chunk of the largest count",
                SimulationSnapshotWire(65534, 65535, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/chunk count 0", SimulationSnapshotWire(0, 0, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/chunk index equal to count", SimulationSnapshotWire(1, 1, EntitySnapshotWire()));
            r.Rejects("Replication/SimulationSnapshot/no entities", SimulationSnapshotWire(0, 1));
            r.Accepts(
                "Replication/SimulationSnapshot/24 entities",
                SimulationSnapshotWire(0, 1, Enumerable.Range(1, 24).Select(index => EntitySnapshotWire((ulong)index)).ToArray()));
            r.Rejects(
                "Replication/SimulationSnapshot/25 entities",
                SimulationSnapshotWire(0, 1, Enumerable.Range(1, 25).Select(index => EntitySnapshotWire((ulong)index)).ToArray()));
            r.Rejects("Replication/SimulationSnapshot/entity zero", SimulationSnapshotWire(0, 1, EntitySnapshotWire(0)));
            r.Accepts(
                "Replication/SimulationSnapshot/duplicate entities",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(5), EntitySnapshotWire(5)));
            r.Rejects(
                "Replication/SimulationSnapshot/NaN entity state",
                SimulationSnapshotWire(0, 1, EntitySnapshotWire(5, PlayerStateWireWithBits(2, QuietNaNBits))));

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
                "Replication/SimulationSnapshot/25 entities",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    Enumerable.Range(1, 25).Select(index => EntitySnapshotModel((ulong)index)).ToArray())));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/null entity",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 1, new RealtimeEntitySnapshot[] { null })));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/entity zero",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(0, 1, EntitySnapshotModel(0))));
            r.RejectsEncode(
                "Replication/SimulationSnapshot/null entity state",
                () => RealtimeProtocol.EncodeSimulationSnapshot(SimulationSnapshotModel(
                    0,
                    1,
                    new RealtimeEntitySnapshot(entityId: 1, lastProcessedInputSequence: 2, state: null))));
        }

        private static RealtimeEntitySpawn EntitySpawnModel(
            ulong entityId = 0x0A0B0C0D0E0F1011,
            RealtimeEntityKind kind = RealtimeEntityKind.Player,
            string persistentId = PersistentPlayerId,
            string displayName = "Grön Ödla",
            string archetypeId = "player-default",
            Optional<RealtimePlayerState> state = default)
        {
            return new RealtimeEntitySpawn(
                entityId: entityId,
                kind: kind,
                persistentId: persistentId,
                displayName: displayName,
                archetypeId: archetypeId,
                serverTick: 123456,
                initialState: state.Or(PlayerStateModel()));
        }

        private static RealtimeEntitySnapshot EntitySnapshotModel(ulong entityId)
        {
            return new RealtimeEntitySnapshot(
                entityId: entityId,
                lastProcessedInputSequence: (uint)(entityId % 997) + 9,
                state: PlayerStateModel());
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
            Wire state = null)
        {
            return Wire.Message(RealtimeMessageType.EntitySpawn)
                .U64(entityId)
                .U8(kind)
                .Text(persistentId ?? PersistentPlayerId)
                .Text(displayName ?? "Grön Ödla")
                .Text(archetypeId ?? "player-default")
                .U32(123456)
                .Append(state ?? PlayerStateWire());
        }

        private static Wire EntityDespawnWire(ulong entityId = 7, WireText reason = null)
        {
            return Wire.Message(RealtimeMessageType.EntityDespawn).U64(entityId).Text(reason ?? "out_of_interest");
        }

        private static Wire EntitySnapshotWire(ulong entityId = 42, Wire state = null)
        {
            return Wire.Fragment()
                .U64(entityId)
                .U32((uint)(entityId % 997) + 9)
                .Append(state ?? PlayerStateWire());
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
