using System.Collections.Generic;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSceneContext : MonoBehaviour
    {
        [SerializeField] private LocalPlayerController localPlayer;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;
        [SerializeField] private Transform entityPresentationRoot;

        private readonly Dictionary<ulong, RemotePlayerView> remotePlayers =
            new Dictionary<ulong, RemotePlayerView>();
        private RealtimeWorldClient worldClient;

        private void Start()
        {
            if (!ValidateReferences())
            {
                enabled = false;
                return;
            }

            if (ShooterMmoClientSession.ActiveWorldSession != null)
            {
                worldClient = ShooterMmoClientBootstrap.WorldClient;
                if (worldClient == null
                    || worldClient.MovementSession == null
                    || !localPlayer.EnableServerAuthoritativeMovement(worldClient))
                {
                    Debug.LogError(
                        "WorldSceneContext cannot start server-authoritative movement because the realtime movement session is unavailable.",
                        this);
                    enabled = false;
                    return;
                }

                worldClient.EntitySpawned += OnEntitySpawned;
                worldClient.EntityDespawned += OnEntityDespawned;
                worldClient.WorldSnapshotReceived += OnWorldSnapshotReceived;
                foreach (var spawn in worldClient.SpawnedEntities)
                {
                    OnEntitySpawned(spawn);
                }
            }
            else
            {
                localPlayer.Teleport(playerSpawnPoint.position, playerSpawnPoint.rotation);
            }

            localPlayer.PlayerCamera.SetTarget(localPlayer.CameraTarget, localPlayer.PlayerInput);
        }

        private void OnDestroy()
        {
            if (worldClient != null)
            {
                worldClient.EntitySpawned -= OnEntitySpawned;
                worldClient.EntityDespawned -= OnEntityDespawned;
                worldClient.WorldSnapshotReceived -= OnWorldSnapshotReceived;
            }

            remotePlayers.Clear();

            if (localPlayer != null)
            {
                localPlayer.DisableServerAuthoritativeMovement();
            }
        }

        private void OnEntitySpawned(RealtimeEntitySpawn spawn)
        {
            var movementSession = worldClient != null ? worldClient.MovementSession : null;
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

        private void OnWorldSnapshotReceived(RealtimeWorldSnapshot snapshot)
        {
            var movementSession = worldClient != null ? worldClient.MovementSession : null;
            if (movementSession == null)
            {
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
            if (localPlayer == null
                || playerSpawnPoint == null
                || remotePlayerPrefab == null
                || entityPresentationRoot == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires a local player, spawn point, remote player prefab, and entity presentation root.",
                    this);
                return false;
            }

            if (entityPresentationRoot.IsChildOf(localPlayer.transform))
            {
                Debug.LogError(
                    "WorldSceneContext entity presentation root must not be owned by the local player.",
                    this);
                return false;
            }

            if (localPlayer.PlayerCamera == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires the local player prefab to own its player camera.",
                    localPlayer);
                return false;
            }

            if (!localPlayer.PlayerInput.Initialize())
            {
                Debug.LogError(
                    "WorldSceneContext cannot initialize because the local player input is not configured.",
                    localPlayer);
                return false;
            }

            return true;
        }
    }
}
