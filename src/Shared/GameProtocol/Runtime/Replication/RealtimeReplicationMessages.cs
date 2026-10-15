namespace Mmorpg.GameProtocol
{
    public sealed class RealtimeEntitySpawn
    {
        public RealtimeEntitySpawn(
            ulong entityId,
            RealtimeEntityKind kind,
            string persistentId,
            string displayName,
            string archetypeId,
            uint serverTick,
            RealtimePlayerState initialState)
        {
            EntityId = entityId;
            Kind = kind;
            PersistentId = persistentId;
            DisplayName = displayName;
            ArchetypeId = archetypeId;
            ServerTick = serverTick;
            InitialState = initialState;
        }

        public ulong EntityId { get; }

        public RealtimeEntityKind Kind { get; }

        public string PersistentId { get; }

        public string DisplayName { get; }

        public string ArchetypeId { get; }

        public uint ServerTick { get; }

        public RealtimePlayerState InitialState { get; }
    }

    public sealed class RealtimeEntityDespawn
    {
        public RealtimeEntityDespawn(ulong entityId, string reason)
        {
            EntityId = entityId;
            Reason = reason;
        }

        public ulong EntityId { get; }

        public string Reason { get; }
    }

    public sealed class RealtimeEntitySnapshot
    {
        public RealtimeEntitySnapshot(
            ulong entityId,
            uint lastProcessedInputSequence,
            RealtimePlayerState state)
        {
            EntityId = entityId;
            LastProcessedInputSequence = lastProcessedInputSequence;
            State = state;
        }

        public ulong EntityId { get; }

        public uint LastProcessedInputSequence { get; }

        public RealtimePlayerState State { get; }
    }

    public sealed class RealtimeSimulationSnapshot
    {
        public RealtimeSimulationSnapshot(
            uint snapshotSequence,
            uint serverTick,
            ushort chunkIndex,
            ushort chunkCount,
            RealtimeEntitySnapshot[] entities)
        {
            SnapshotSequence = snapshotSequence;
            ServerTick = serverTick;
            ChunkIndex = chunkIndex;
            ChunkCount = chunkCount;
            Entities = entities;
        }

        public uint SnapshotSequence { get; }

        public uint ServerTick { get; }

        public ushort ChunkIndex { get; }

        public ushort ChunkCount { get; }

        public RealtimeEntitySnapshot[] Entities { get; }
    }
}
