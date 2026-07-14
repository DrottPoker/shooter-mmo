using System.Collections.Generic;
using System.Linq;
using ShooterMmo.GameSimulation;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSceneContext : MonoBehaviour
    {
        [SerializeField] private LocalPlayerController localPlayerPrefab;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;
        [SerializeField] private Transform entityPresentationRoot;

        private readonly Dictionary<ulong, RemotePlayerView> remotePlayers =
            new Dictionary<ulong, RemotePlayerView>();
        private RealtimeSimulationClient simulationClient;
        private LocalPlayerController localPlayer;

        private void Start()
        {
            if (!ValidateReferences())
            {
                enabled = false;
                return;
            }

            if (ShooterMmoClientSession.ActiveSimulationSession == null)
            {
                enabled = false;
                return;
            }

            simulationClient = ShooterMmoClientBootstrap.SimulationClient;
            if (simulationClient == null
                || !simulationClient.IsJoined
                || simulationClient.MovementSession == null)
            {
                Debug.LogError(
                    "WorldSceneContext cannot spawn the local player because the joined realtime movement session is unavailable.",
                    this);
                enabled = false;
                return;
            }

            if (!TrySpawnLocalPlayer())
            {
                enabled = false;
                return;
            }

            if (!localPlayer.EnableServerAuthoritativeMovement(simulationClient))
            {
                Debug.LogError(
                    "WorldSceneContext cannot start server-authoritative movement for the joined local player.",
                    this);
                DestroyLocalPlayer();
                enabled = false;
                return;
            }

            simulationClient.EntitySpawned += OnEntitySpawned;
            simulationClient.EntityDespawned += OnEntityDespawned;
            simulationClient.SimulationSnapshotReceived += OnSimulationSnapshotReceived;
            foreach (var spawn in simulationClient.SpawnedEntities)
            {
                OnEntitySpawned(spawn);
            }
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.EntitySpawned -= OnEntitySpawned;
                simulationClient.EntityDespawned -= OnEntityDespawned;
                simulationClient.SimulationSnapshotReceived -= OnSimulationSnapshotReceived;
            }

            remotePlayers.Clear();

            DestroyLocalPlayer();
        }

        private void OnEntitySpawned(RealtimeEntitySpawn spawn)
        {
            var movementSession = simulationClient != null ? simulationClient.MovementSession : null;
            if (movementSession == null || spawn.EntityId == movementSession.ControlledEntityId)
            {
                return;
            }

            if (spawn.Kind != RealtimeEntityKind.Player
                || !string.Equals(
                    spawn.ArchetypeId,
                    RemotePlayerView.SupportedArchetypeId,
                    System.StringComparison.Ordinal))
            {
                Debug.LogWarning(
                    "WorldSceneContext cannot present unsupported entity archetype '"
                    + spawn.ArchetypeId + "' for network entity '" + spawn.EntityId + "'.",
                    this);
                return;
            }

            if (!movementSession.TryEnsureCollisionChunks(
                    new[]
                    {
                        new SimulationVector3(
                            spawn.InitialState.PositionX,
                            spawn.InitialState.PositionY,
                            spawn.InitialState.PositionZ)
                    },
                    out var collisionError))
            {
                simulationClient.DisconnectForClientFailure(
                    "collision_stream_failed",
                    collisionError);
                return;
            }

            if (!remotePlayers.TryGetValue(spawn.EntityId, out var remotePlayer))
            {
                remotePlayer = Instantiate(remotePlayerPrefab, entityPresentationRoot);
                var delayTicks = Mathf.Max(
                    2,
                    Mathf.CeilToInt(movementSession.Settings.TickRateHz * 0.1f));
                remotePlayer.Initialize(
                    spawn.EntityId,
                    spawn.PersistentId,
                    spawn.DisplayName,
                    spawn.ArchetypeId,
                    movementSession.Settings.TickRateHz,
                    delayTicks,
                    movementSession.CollisionWorld,
                    movementSession.Settings.CharacterCollision);
                remotePlayers.Add(spawn.EntityId, remotePlayer);
            }

            remotePlayer.PushSnapshot(spawn.ServerTick, spawn.InitialState);
        }

        private void OnEntityDespawned(RealtimeEntityDespawn despawn)
        {
            if (!remotePlayers.TryGetValue(despawn.EntityId, out var remotePlayer))
            {
                return;
            }

            remotePlayers.Remove(despawn.EntityId);
            Destroy(remotePlayer.gameObject);
        }

        private void OnSimulationSnapshotReceived(RealtimeSimulationSnapshot snapshot)
        {
            var movementSession = simulationClient != null ? simulationClient.MovementSession : null;
            if (movementSession == null)
            {
                return;
            }

            var collisionAnchors = snapshot.Entities
                .Select(entity => new SimulationVector3(
                    entity.State.PositionX,
                    entity.State.PositionY,
                    entity.State.PositionZ));
            if (!movementSession.TryRefreshCollisionStreaming(
                    collisionAnchors,
                    out var collisionError))
            {
                simulationClient.DisconnectForClientFailure(
                    "collision_stream_failed",
                    collisionError);
                return;
            }

            for (var index = 0; index < snapshot.Entities.Length; index++)
            {
                var entity = snapshot.Entities[index];
                if (entity.EntityId == movementSession.ControlledEntityId)
                {
                    continue;
                }

                if (!remotePlayers.TryGetValue(entity.EntityId, out var remotePlayer))
                {
                    continue;
                }

                remotePlayer.PushSnapshot(snapshot.ServerTick, entity.State);
            }
        }

        private bool ValidateReferences()
        {
            if (localPlayerPrefab == null
                || playerSpawnPoint == null
                || remotePlayerPrefab == null
                || entityPresentationRoot == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires a local player prefab, spawn point, remote player prefab, and entity presentation root.",
                    this);
                return false;
            }

            if (localPlayerPrefab.gameObject.scene.IsValid())
            {
                Debug.LogError(
                    "WorldSceneContext local player reference must be a prefab asset, not a scene instance.",
                    this);
                return false;
            }

            if (playerSpawnPoint.parent == null)
            {
                Debug.LogError(
                    "WorldSceneContext player spawn point must have a scene parent for runtime players.",
                    playerSpawnPoint);
                return false;
            }

            if (localPlayerPrefab.PlayerCamera == null
                || localPlayerPrefab.GetComponent<LocalPlayerInput>() == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires the local player prefab to own its input and player camera.",
                    localPlayerPrefab);
                return false;
            }

            return true;
        }

        private bool TrySpawnLocalPlayer()
        {
            localPlayer = Instantiate(
                localPlayerPrefab,
                playerSpawnPoint.position,
                playerSpawnPoint.rotation,
                playerSpawnPoint.parent);
            localPlayer.name = localPlayerPrefab.name;

            if (localPlayer.PlayerCamera == null
                || localPlayer.PlayerInput == null
                || !localPlayer.PlayerInput.Initialize())
            {
                Debug.LogError(
                    "WorldSceneContext cannot initialize the spawned local player input and camera.",
                    localPlayer);
                DestroyLocalPlayer();
                return false;
            }

            localPlayer.PlayerCamera.SetTarget(localPlayer.CameraTarget, localPlayer.PlayerInput);
            return true;
        }

        private void DestroyLocalPlayer()
        {
            if (localPlayer == null)
            {
                return;
            }

            localPlayer.DisableServerAuthoritativeMovement();
            Destroy(localPlayer.gameObject);
            localPlayer = null;
        }
    }
}
