namespace Mmorpg.GameProtocol
{
    public sealed class RealtimeJoinAccepted
    {
        public RealtimeJoinAccepted(
            string simulationSessionId,
            string accountId,
            string characterId,
            string characterName,
            string shardId,
            string worldId,
            ulong controlledEntityId,
            string simulationRevision,
            string collisionRevision,
            string joinedAt,
            string sessionExpiresAt,
            bool isReconnect,
            RealtimeCarryState carryState,
            RealtimeMovementSettings movementSettings,
            RealtimePlayerState initialPlayerState)
        {
            SimulationSessionId = simulationSessionId;
            AccountId = accountId;
            CharacterId = characterId;
            CharacterName = characterName;
            ShardId = shardId;
            WorldId = worldId;
            ControlledEntityId = controlledEntityId;
            SimulationRevision = simulationRevision;
            CollisionRevision = collisionRevision;
            JoinedAt = joinedAt;
            SessionExpiresAt = sessionExpiresAt;
            IsReconnect = isReconnect;
            CarryState = carryState;
            MovementSettings = movementSettings;
            InitialPlayerState = initialPlayerState;
        }

        public string SimulationSessionId { get; }

        public string AccountId { get; }

        public string CharacterId { get; }

        public string CharacterName { get; }

        public string ShardId { get; }

        public string WorldId { get; }

        public ulong ControlledEntityId { get; }

        public string SimulationRevision { get; }

        public string CollisionRevision { get; }

        public string JoinedAt { get; }

        public string SessionExpiresAt { get; }

        public bool IsReconnect { get; }

        public RealtimeCarryState CarryState { get; }

        public RealtimeMovementSettings MovementSettings { get; }

        public RealtimePlayerState InitialPlayerState { get; }
    }
}
