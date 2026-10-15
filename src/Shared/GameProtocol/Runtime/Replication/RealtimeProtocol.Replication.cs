using System;
using System.IO;

namespace Mmorpg.GameProtocol
{
    // Entity presence and the shared simulation snapshot of interest-visible entities.
    public static partial class RealtimeProtocol
    {
        public const int SimulationSnapshotPacketHeaderSize = 20;
        public const int SimulationSnapshotEntitySize = 41;

        public static byte[] EncodeEntitySpawn(RealtimeEntitySpawn spawn)
        {
            if (!IsValidEntitySpawn(spawn))
            {
                throw new ArgumentException("Entity spawn is invalid.", nameof(spawn));
            }

            return Encode(RealtimeMessageType.EntitySpawn, writer =>
            {
                writer.Write(spawn.EntityId);
                writer.Write((byte)spawn.Kind);
                WriteString(writer, spawn.PersistentId, MaximumIdentifierLength, nameof(spawn.PersistentId));
                WriteString(writer, spawn.DisplayName, MaximumNameLength, nameof(spawn.DisplayName));
                WriteString(writer, spawn.ArchetypeId, MaximumIdentifierLength, nameof(spawn.ArchetypeId));
                writer.Write(spawn.ServerTick);
                WritePlayerState(writer, spawn.InitialState);
            });
        }

        public static bool TryDecodeEntitySpawn(
            byte[] data,
            out RealtimeEntitySpawn spawn,
            out string error)
        {
            spawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.EntitySpawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || !TryReadByte(reader, out var rawKind, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var persistentId, out error)
                    || !TryReadString(reader, MaximumNameLength, out var displayName, out error)
                    || !TryReadString(reader, MaximumIdentifierLength, out var archetypeId, out error)
                    || !TryReadUInt32(reader, out var serverTick, out error)
                    || !TryReadPlayerState(reader, out var initialState, out error)
                    || !TryFinish(stream, out error))
                {
                    return false;
                }

                var decoded = new RealtimeEntitySpawn(
                    entityId,
                    (RealtimeEntityKind)rawKind,
                    persistentId,
                    displayName,
                    archetypeId,
                    serverTick,
                    initialState);
                if (!IsValidEntitySpawn(decoded))
                {
                    error = "Entity spawn is invalid.";
                    return false;
                }

                spawn = decoded;
                return true;
            }
        }

        public static byte[] EncodeEntityDespawn(RealtimeEntityDespawn despawn)
        {
            if (despawn == null || despawn.EntityId == 0 || string.IsNullOrWhiteSpace(despawn.Reason))
            {
                throw new ArgumentException("Entity despawn is invalid.", nameof(despawn));
            }

            return Encode(RealtimeMessageType.EntityDespawn, writer =>
            {
                writer.Write(despawn.EntityId);
                WriteString(writer, despawn.Reason, MaximumErrorCodeLength, nameof(despawn.Reason));
            });
        }

        public static bool TryDecodeEntityDespawn(
            byte[] data,
            out RealtimeEntityDespawn despawn,
            out string error)
        {
            despawn = null;
            if (!TryCreateReader(data, RealtimeMessageType.EntityDespawn, out var stream, out var reader, out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt64(reader, out var entityId, out error)
                    || entityId == 0
                    || !TryReadString(reader, MaximumErrorCodeLength, out var reason, out error)
                    || string.IsNullOrWhiteSpace(reason)
                    || !TryFinish(stream, out error))
                {
                    error = string.IsNullOrEmpty(error) ? "Entity despawn is invalid." : error;
                    return false;
                }

                despawn = new RealtimeEntityDespawn(entityId, reason);
                return true;
            }
        }

        public static byte[] EncodeSimulationSnapshot(RealtimeSimulationSnapshot snapshot)
        {
            if (snapshot == null
                || snapshot.ChunkCount == 0
                || snapshot.ChunkIndex >= snapshot.ChunkCount
                || snapshot.Entities == null
                || snapshot.Entities.Length == 0
                || snapshot.Entities.Length > MaximumSnapshotEntitiesPerChunk)
            {
                throw new ArgumentException("Simulation snapshot metadata is invalid.", nameof(snapshot));
            }

            return Encode(RealtimeMessageType.SimulationSnapshot, writer =>
            {
                writer.Write(snapshot.SnapshotSequence);
                writer.Write(snapshot.ServerTick);
                writer.Write(snapshot.ChunkIndex);
                writer.Write(snapshot.ChunkCount);
                writer.Write((byte)snapshot.Entities.Length);
                for (var index = 0; index < snapshot.Entities.Length; index++)
                {
                    WriteEntitySnapshot(writer, snapshot.Entities[index]);
                }
            });
        }

        public static bool TryDecodeSimulationSnapshot(
            byte[] data,
            out RealtimeSimulationSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryCreateReader(
                    data,
                    RealtimeMessageType.SimulationSnapshot,
                    out var stream,
                    out var reader,
                    out error))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                if (!TryReadUInt32(reader, out var snapshotSequence, out error)
                    || !TryReadUInt32(reader, out var serverTick, out error)
                    || !TryReadUInt16(reader, out var chunkIndex, out error)
                    || !TryReadUInt16(reader, out var chunkCount, out error)
                    || !TryReadByte(reader, out var entityCount, out error))
                {
                    return false;
                }

                if (chunkCount == 0
                    || chunkIndex >= chunkCount
                    || entityCount == 0
                    || entityCount > MaximumSnapshotEntitiesPerChunk)
                {
                    error = "Simulation snapshot metadata is invalid.";
                    return false;
                }

                var entities = new RealtimeEntitySnapshot[entityCount];
                for (var index = 0; index < entities.Length; index++)
                {
                    if (!TryReadEntitySnapshot(reader, out entities[index], out error))
                    {
                        return false;
                    }
                }

                if (!TryFinish(stream, out error))
                {
                    return false;
                }

                snapshot = new RealtimeSimulationSnapshot(
                    snapshotSequence,
                    serverTick,
                    chunkIndex,
                    chunkCount,
                    entities);
                return true;
            }
        }

        private static void WriteEntitySnapshot(BinaryWriter writer, RealtimeEntitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.EntityId == 0)
            {
                throw new ArgumentException("Entity snapshot id is invalid.", nameof(snapshot));
            }

            writer.Write(snapshot.EntityId);
            writer.Write(snapshot.LastProcessedInputSequence);
            WritePlayerState(writer, snapshot.State);
        }

        private static bool TryReadEntitySnapshot(
            BinaryReader reader,
            out RealtimeEntitySnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryReadUInt64(reader, out var entityId, out error)
                || !TryReadUInt32(reader, out var lastProcessedInputSequence, out error)
                || !TryReadPlayerState(reader, out var state, out error))
            {
                return false;
            }

            if (entityId == 0)
            {
                error = "Entity snapshot id is invalid.";
                return false;
            }

            snapshot = new RealtimeEntitySnapshot(entityId, lastProcessedInputSequence, state);
            return true;
        }

        private static bool IsValidEntitySpawn(RealtimeEntitySpawn spawn)
        {
            return spawn != null
                && spawn.EntityId != 0
                && Enum.IsDefined(typeof(RealtimeEntityKind), spawn.Kind)
                && Guid.TryParse(spawn.PersistentId, out _)
                && !string.IsNullOrWhiteSpace(spawn.DisplayName)
                && !string.IsNullOrWhiteSpace(spawn.ArchetypeId)
                && IsValidPlayerState(spawn.InitialState);
        }
    }
}
